using System;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Ffxiv2Mqtt.Services;

/// <summary>
/// Sends a string into the game's native chat input pipeline,
/// exactly as if the player had typed it and pressed Enter.
///
/// This allows commands like /gearset, /micon, /ac, /wait, as well as
/// any text that native macros support.
///
/// Must be called on the Framework (main game) thread.
/// </summary>
public static unsafe class ChatBoxHelper
{
    private static readonly ProcessChatBoxDelegate? _processChatBox;
    private delegate void ProcessChatBoxDelegate(UIModule* uiModule, nint message, nint unused, byte a4);

    static ChatBoxHelper()
    {
        _processChatBox = GetProcessChatBox();
    }

    private static ProcessChatBoxDelegate? GetProcessChatBox()
    {
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null) return null;
            // VTable slot 26 = ProcessChatBoxInput (same as SomethingNeedDoing, QoLBar)
            const int VTableSlot = 26;
            var vtable = *(nint**)uiModule;
            var fnPtr  = vtable[VTableSlot];
            return Marshal.GetDelegateForFunctionPointer<ProcessChatBoxDelegate>(fnPtr);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends <paramref name="message"/> into the game chat/command processor.
    /// Call this on the Framework thread only.
    /// </summary>
    public static void SendMessage(string message)
    {
        if (_processChatBox == null)
            throw new InvalidOperationException("ChatBoxHelper: ProcessChatBox function pointer not resolved.");

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            throw new InvalidOperationException("ChatBoxHelper: UIModule not available.");

        var bytes = Encoding.UTF8.GetBytes(message + "\0");
        fixed (byte* ptr = bytes)
        {
            using var mem = new LocalMemory(bytes.Length + 30);
            mem.Write(ptr, bytes.Length);
            _processChatBox(uiModule, mem.Address, nint.Zero, 0);
        }
    }

    private sealed class LocalMemory : IDisposable
    {
        public nint Address { get; }
        public LocalMemory(int length)
        {
            Address = Marshal.AllocHGlobal(length);
            for (var i = 0; i < length; i++)
                Marshal.WriteByte(Address, i, 0);
        }
        public unsafe void Write(byte* src, int count)
        {
            for (var i = 0; i < count; i++)
                Marshal.WriteByte(Address, i, src[i]);
        }
        public void Dispose() => Marshal.FreeHGlobal(Address);
    }
}