using System.Runtime.InteropServices;
using DeskRecall.Core.Compatibility;
using DeskRecall.Platform.Interop;
using DeskRecall.Platform.VirtualDesktops.Com;

namespace DeskRecall.Platform.VirtualDesktops;

/// <summary>Gemeinsame Operationen eines Schnittstellensatzes. Alle Aufrufe laufen auf dem COM-Thread.</summary>
internal interface IVdBackend
{
    VirtualDesktopApiSet ApiSet { get; }
    int GetCount();
    List<(Guid Id, string Name)> GetDesktops();
    Guid GetCurrentId();
    void Switch(Guid id);
    Guid Create();
    void Remove(Guid id, Guid fallback);
    void Move(Guid id, int index);
    void Rename(Guid id, string name);
    void MoveView(IntPtr view, Guid id);
}

internal static class BackendFactory
{
    public static IVdBackend? Create(VirtualDesktopApiSet set, IServiceProvider10 shell)
    {
        object Query(Type t)
        {
            var clsid = Clsid.VirtualDesktopManagerInternal;
            var iid = t.GUID;
            return shell.QueryService(ref clsid, ref iid);
        }
        return set switch
        {
            VirtualDesktopApiSet.Win11_24H2 => new Backend24H2((IVirtualDesktopManagerInternal24H2)Query(typeof(IVirtualDesktopManagerInternal24H2))),
            VirtualDesktopApiSet.Win11_23H2 => new Backend23H2((IVirtualDesktopManagerInternal23H2)Query(typeof(IVirtualDesktopManagerInternal23H2))),
            VirtualDesktopApiSet.Win11_22H2 => new Backend22H2((IVirtualDesktopManagerInternal22H2)Query(typeof(IVirtualDesktopManagerInternal22H2))),
            VirtualDesktopApiSet.Win11Legacy => new BackendLegacy((IVirtualDesktopManagerInternalLegacy)Query(typeof(IVirtualDesktopManagerInternalLegacy))),
            _ => null,
        };
    }

    internal static List<T> Enumerate<T>(IObjectArray array) where T : class
    {
        var list = new List<T>();
        try
        {
            array.GetCount(out var count);
            var iid = typeof(T).GUID;
            for (var i = 0; i < count; i++)
            {
                array.GetAt(i, ref iid, out var obj);
                list.Add((T)obj);
            }
        }
        finally { Marshal.ReleaseComObject(array); }
        return list;
    }
}

internal abstract class Backend11Base : IVdBackend
{
    public abstract VirtualDesktopApiSet ApiSet { get; }
    protected abstract IObjectArray RawDesktops();
    protected abstract IVirtualDesktop11 Current();
    protected abstract IVirtualDesktop11 Find(Guid id);
    protected abstract void DoSwitch(IVirtualDesktop11 d);
    protected abstract IVirtualDesktop11 DoCreate();
    protected abstract void DoRemove(IVirtualDesktop11 d, IVirtualDesktop11 fallback);
    protected abstract void DoMove(IVirtualDesktop11 d, int index);
    protected abstract void DoRename(IVirtualDesktop11 d, IntPtr hstring);
    protected abstract void DoMoveView(IntPtr view, IVirtualDesktop11 d);
    public abstract int GetCount();

    public List<(Guid Id, string Name)> GetDesktops() =>
        BackendFactory.Enumerate<IVirtualDesktop11>(RawDesktops()).Select(d => (d.GetId(), NativeMethods.HStringToString(d.GetName()) ?? "")).ToList();

    public Guid GetCurrentId() => Current().GetId();
    public void Switch(Guid id) => DoSwitch(Find(id));
    public Guid Create() => DoCreate().GetId();
    public void Remove(Guid id, Guid fallback) => DoRemove(Find(id), Find(fallback));
    public void Move(Guid id, int index) => DoMove(Find(id), index);
    public void Rename(Guid id, string name)
    {
        var h = NativeMethods.CreateHString(name);
        try { DoRename(Find(id), h); } finally { NativeMethods.WindowsDeleteString(h); }
    }
    public void MoveView(IntPtr view, Guid id) => DoMoveView(view, Find(id));
}

internal sealed class Backend24H2(IVirtualDesktopManagerInternal24H2 m) : Backend11Base
{
    public override VirtualDesktopApiSet ApiSet => VirtualDesktopApiSet.Win11_24H2;
    public override int GetCount() => m.GetCount();
    protected override IObjectArray RawDesktops() { m.GetDesktops(out var a); return a; }
    protected override IVirtualDesktop11 Current() => m.GetCurrentDesktop();
    protected override IVirtualDesktop11 Find(Guid id) => m.FindDesktop(ref id);
    protected override void DoSwitch(IVirtualDesktop11 d) => m.SwitchDesktop(d);
    protected override IVirtualDesktop11 DoCreate() => m.CreateDesktop();
    protected override void DoRemove(IVirtualDesktop11 d, IVirtualDesktop11 f) => m.RemoveDesktop(d, f);
    protected override void DoMove(IVirtualDesktop11 d, int i) => m.MoveDesktop(d, i);
    protected override void DoRename(IVirtualDesktop11 d, IntPtr h) => m.SetDesktopName(d, h);
    protected override void DoMoveView(IntPtr v, IVirtualDesktop11 d) => m.MoveViewToDesktop(v, d);
}

internal sealed class Backend23H2(IVirtualDesktopManagerInternal23H2 m) : Backend11Base
{
    public override VirtualDesktopApiSet ApiSet => VirtualDesktopApiSet.Win11_23H2;
    public override int GetCount() => m.GetCount();
    protected override IObjectArray RawDesktops() { m.GetDesktops(out var a); return a; }
    protected override IVirtualDesktop11 Current() => m.GetCurrentDesktop();
    protected override IVirtualDesktop11 Find(Guid id) => m.FindDesktop(ref id);
    protected override void DoSwitch(IVirtualDesktop11 d) => m.SwitchDesktop(d);
    protected override IVirtualDesktop11 DoCreate() => m.CreateDesktop();
    protected override void DoRemove(IVirtualDesktop11 d, IVirtualDesktop11 f) => m.RemoveDesktop(d, f);
    protected override void DoMove(IVirtualDesktop11 d, int i) => m.MoveDesktop(d, i);
    protected override void DoRename(IVirtualDesktop11 d, IntPtr h) => m.SetDesktopName(d, h);
    protected override void DoMoveView(IntPtr v, IVirtualDesktop11 d) => m.MoveViewToDesktop(v, d);
}

internal sealed class Backend22H2(IVirtualDesktopManagerInternal22H2 m) : Backend11Base
{
    public override VirtualDesktopApiSet ApiSet => VirtualDesktopApiSet.Win11_22H2;
    public override int GetCount() => m.GetCount();
    protected override IObjectArray RawDesktops() { m.GetDesktops(out var a); return a; }
    protected override IVirtualDesktop11 Current() => m.GetCurrentDesktop();
    protected override IVirtualDesktop11 Find(Guid id) => m.FindDesktop(ref id);
    protected override void DoSwitch(IVirtualDesktop11 d) => m.SwitchDesktop(d);
    protected override IVirtualDesktop11 DoCreate() => m.CreateDesktop();
    protected override void DoRemove(IVirtualDesktop11 d, IVirtualDesktop11 f) => m.RemoveDesktop(d, f);
    protected override void DoMove(IVirtualDesktop11 d, int i) => m.MoveDesktop(d, i);
    protected override void DoRename(IVirtualDesktop11 d, IntPtr h) => m.SetDesktopName(d, h);
    protected override void DoMoveView(IntPtr v, IVirtualDesktop11 d) => m.MoveViewToDesktop(v, d);
}

internal sealed class BackendLegacy(IVirtualDesktopManagerInternalLegacy m) : IVdBackend
{
    public VirtualDesktopApiSet ApiSet => VirtualDesktopApiSet.Win11Legacy;
    public int GetCount() => m.GetCount(IntPtr.Zero);

    public List<(Guid Id, string Name)> GetDesktops()
    {
        m.GetDesktops(IntPtr.Zero, out var a);
        return BackendFactory.Enumerate<IVirtualDesktopLegacy>(a).Select(d => (d.GetId(), NativeMethods.HStringToString(d.GetName()) ?? "")).ToList();
    }

    private IVirtualDesktopLegacy Find(Guid id) => m.FindDesktop(ref id);
    public Guid GetCurrentId() => m.GetCurrentDesktop(IntPtr.Zero).GetId();
    public void Switch(Guid id) => m.SwitchDesktop(IntPtr.Zero, Find(id));
    public Guid Create() => m.CreateDesktop(IntPtr.Zero).GetId();
    public void Remove(Guid id, Guid fallback) => m.RemoveDesktop(Find(id), Find(fallback));
    public void Move(Guid id, int index) => m.MoveDesktop(Find(id), IntPtr.Zero, index);
    public void Rename(Guid id, string name)
    {
        var h = NativeMethods.CreateHString(name);
        try { m.SetDesktopName(Find(id), h); } finally { NativeMethods.WindowsDeleteString(h); }
    }
    public void MoveView(IntPtr view, Guid id) => m.MoveViewToDesktop(view, Find(id));
}
