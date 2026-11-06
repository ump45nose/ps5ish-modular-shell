using System;
using System.Runtime.InteropServices;

namespace ModularShell.PlayniteIntegration
{
    /// <summary>只读取 XInput，不注入系统按键；失去前台时由调用方停止消费。</summary>
    public sealed class ControllerInput
    {
        [StructLayout(LayoutKind.Sequential)] private struct Gamepad
        {
            public ushort buttons; public byte leftTrigger, rightTrigger; public short leftX, leftY, rightX, rightY;
        }
        [StructLayout(LayoutKind.Sequential)] private struct State { public uint packet; public Gamepad gamepad; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint GetState(uint index, out State state);
        private ushort previous;
        private DateTime repeatAt;
        public event Action<string> Action;

        /// <summary>离开消费界面时清除旧按键状态，不把过期动作带入下一次进入。</summary>
        public void Reset() { previous = 0; repeatAt = DateTime.MinValue; }

        /// <summary>Playnite 用零 lParam 的 SendMessage 发送方向键；真实键盘包含扫描码与重复计数。</summary>
        public static bool IsSyntheticNavigationMessage(int message, IntPtr key, IntPtr flags)
        {
            return (message==0x100||message==0x101) && flags==IntPtr.Zero && key.ToInt64()>=0x25 && key.ToInt64()<=0x28;
        }

        /// <summary>把 SDK 的跨设备按键语义转换成共用状态机输入。</summary>
        public static ushort MapSdk(Playnite.SDK.Events.ControllerInput input)
        {
            switch (input)
            {
                case Playnite.SDK.Events.ControllerInput.A: return 0x1000;
                case Playnite.SDK.Events.ControllerInput.B: return 0x2000;
                case Playnite.SDK.Events.ControllerInput.X: return 0x4000;
                case Playnite.SDK.Events.ControllerInput.Y: return 0x8000;
                case Playnite.SDK.Events.ControllerInput.DPadLeft:
                case Playnite.SDK.Events.ControllerInput.LeftStickLeft: return 4;
                case Playnite.SDK.Events.ControllerInput.DPadRight:
                case Playnite.SDK.Events.ControllerInput.LeftStickRight: return 8;
                case Playnite.SDK.Events.ControllerInput.DPadUp:
                case Playnite.SDK.Events.ControllerInput.LeftStickUp: return 1;
                case Playnite.SDK.Events.ControllerInput.DPadDown:
                case Playnite.SDK.Events.ControllerInput.LeftStickDown: return 2;
                case Playnite.SDK.Events.ControllerInput.LeftShoulder: return 0x100;
                case Playnite.SDK.Events.ControllerInput.RightShoulder: return 0x200;
                case Playnite.SDK.Events.ControllerInput.Start: return 0x10;
                default: return 0;
            }
        }

        /// <summary>轮询首个已连接手柄；按下边沿触发动作，方向长按有限速重复。</summary>
        public void Poll()
        {
            State state = new State(); bool connected = false;
            try { for (uint i = 0; i < 4; i++) if (GetState(i, out state) == 0) { connected = true; break; } }
            catch (DllNotFoundException) { return; }
            if (!connected) { previous = 0; return; }
            ushort buttons = state.gamepad.buttons;
            if (state.gamepad.leftX < -16000) buttons |= 4;
            if (state.gamepad.leftX > 16000) buttons |= 8;
            if (state.gamepad.leftY > 16000) buttons |= 1;
            if (state.gamepad.leftY < -16000) buttons |= 2;
            Process(buttons, DateTime.UtcNow);
        }
        /// <summary>纯状态转换入口，可用无硬件输入验证去抖与方向重复。</summary>
        public void Process(ushort buttons, DateTime now)
        {
            ushort pressed = (ushort)(buttons & ~previous);
            if (buttons != previous) repeatAt = now.AddMilliseconds(350);
            else if ((buttons & 15) != 0 && now >= repeatAt) { pressed |= (ushort)(buttons & 15); repeatAt = now.AddMilliseconds(120); }
            previous = buttons;
            if (Action == null) return;
            if ((pressed & 4) != 0) Action("left"); else if ((pressed & 8) != 0) Action("right");
            else if ((pressed & 1) != 0) Action("up"); else if ((pressed & 2) != 0) Action("down");
            else if ((pressed & 0x1000) != 0) Action("accept"); else if ((pressed & 0x2000) != 0) Action("back");
            else if ((pressed & 0x4000) != 0) Action("trailer"); else if ((pressed & 0x100) != 0) Action("previousPage");
            else if ((pressed & 0x200) != 0) Action("nextPage");
            else if ((pressed & 0x8010) != 0) Action("settings");
        }
    }
}
