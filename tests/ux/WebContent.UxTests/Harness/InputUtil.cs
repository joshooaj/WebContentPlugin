using System.Runtime.InteropServices;

namespace WebContent.UxTests.Harness;

/// <summary>Low-level input the FlaUI Mouse API does not expose (wheel events).</summary>
public static class InputUtil
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>Sends one mouse wheel notch (delta = ±120) with the pointer over (x, y).</summary>
    public static void Wheel(int x, int y, int delta)
    {
        // Absolute (normalized) coordinates for the wheel event so it lands over the target control.
        var vw = GetSystemMetrics(0); // SM_CXSCREEN
        var vh = GetSystemMetrics(1); // SM_CYSCREEN
        var nx = x * 65535 / Math.Max(1, vw - 1);
        var ny = y * 65535 / Math.Max(1, vh - 1);
        const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000,
                   MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_WHEEL = 0x0800;
        var inputs = new[]
        {
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, mouseData = delta, dwFlags = MOUSEEVENTF_WHEEL | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
