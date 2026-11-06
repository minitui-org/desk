using System.Reflection;
using System.Runtime.CompilerServices;

namespace MiniTui.Vt.Tests;

/// <summary>
/// MiniTui's own input decoder, which is internal to the package, so that round trips test against the real thing.
/// A MiniTui upgrade that renames it fails these tests loudly, rather than silently testing nothing.
/// </summary>
internal sealed class RealDecoder
{
    private const string DecoderType = "MiniTui.AnsiInputDecoder, MiniTui";
    private const string SinkType = "MiniTui.IInputSink, MiniTui";

    private readonly object _decoder = Create(TimeSpan.FromMilliseconds(50));

    public List<object> Decode(ReadOnlySpan<byte> bytes)
    {
        var events = new List<object>();
        var sink = NewSink(events);
        Feed(_decoder, bytes, TimeSpan.Zero, sink);
        Expire(_decoder, TimeSpan.FromSeconds(10), sink);
        return events;
    }

    public static List<ConsoleKeyInfo> Keys(ReadOnlySpan<byte> bytes) =>
        new RealDecoder().Decode(bytes).OfType<ConsoleKeyInfo>().ToList();

    public static List<MouseEvent> Mice(ReadOnlySpan<byte> bytes) =>
        new RealDecoder().Decode(bytes).OfType<MouseEvent>().ToList();

    private static object NewSink(List<object> events)
    {
        var create = typeof(DispatchProxy).GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var proxy = create.MakeGenericMethod(Type.GetType(SinkType, throwOnError: true)!, typeof(SinkProxy)).Invoke(null, null)!;
        ((SinkProxy)proxy).Events = events;
        return proxy;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    [return: UnsafeAccessorType(DecoderType)]
    private static extern object Create(TimeSpan escapeTimeout);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Feed")]
    private static extern void Feed([UnsafeAccessorType(DecoderType)] object decoder, ReadOnlySpan<byte> bytes, TimeSpan now, [UnsafeAccessorType(SinkType)] object sink);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Expire")]
    private static extern void Expire([UnsafeAccessorType(DecoderType)] object decoder, TimeSpan now, [UnsafeAccessorType(SinkType)] object sink);
}

public class SinkProxy : DispatchProxy
{
    internal List<object> Events { get; set; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name is "Key" or "Mouse" or "Paste") Events.Add(args![0]!);
        return null;
    }
}
