// UI layer: macOS-inspired spacing and surfaces, with the original blue palette.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KillAllBack
{
    internal static class MotionCurve
    {
        // Invert x(t), then evaluate y(t): cubic-bezier(.23,1,.32,1).
        internal static double Ease(double x)
        {
            if(x<=0)return 0;if(x>=1)return 1;
            double lo=0,hi=1,t=0;
            for(int i=0;i<24;i++){t=(lo+hi)/2;double a=1-t;double px=3*a*a*t*.23+3*a*t*t*.32+t*t*t;if(px<x)lo=t;else hi=t;}
            return 1-Math.Pow(1-t,3);
        }
    }
    internal sealed class ProgressTrack : ThemedControl
    {
        private readonly Timer timer=new Timer();
        private readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
        private double shown,from,target,changed;
        private bool unknown,reduced,stopped;
        internal event Action<double> Sample;
        internal double Value{get{return shown;}}
        internal bool Running{get{return timer.Enabled;}}
        internal ProgressTrack(){timer.Interval=15;timer.Tick+=delegate{Advance(clock.Elapsed.TotalMilliseconds);Invalidate();};}
        internal void SetTarget(double value,bool indeterminate,bool reduce,bool stop)
        {
            double now=clock.Elapsed.TotalMilliseconds;bool wasUnknown=unknown;Advance(now);
            value=Math.Max(target,Math.Max(0,Math.Min(99,value)));
            if(value!=target||wasUnknown!=indeterminate){from=shown;target=value;changed=now;}
            unknown=indeterminate;reduced=reduce;stopped=stop;
            if(reduced)shown=target;
            if(!stopped&&!reduced&&(unknown||shown<target))timer.Start();else timer.Stop();
            Invalidate();
        }
        internal void Advance(double now)
        {
            if(!unknown)shown=Math.Max(shown,from+(target-from)*MotionCurve.Ease((now-changed)/180));
            if(Sample!=null)Sample(shown);
            if(!unknown&&now-changed>=180)timer.Stop();
        }
        internal void Finish(){timer.Stop();stopped=true;}
        protected override void Dispose(bool disposing){if(disposing)timer.Dispose();base.Dispose(disposing);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            RectangleF r=new RectangleF(0,0,Width,Height);
            using(var p=Theme.Rounded(r,Height/2F))using(var b=new SolidBrush(Theme.Track))g.FillPath(b,p);
            if(unknown)
            {
                double phase=reduced?.5:(clock.Elapsed.TotalMilliseconds%1400)/1400;
                float span=Width*.25F;r.X=(float)((Width+span)*phase-span);r.Width=span;
                var state=g.Save();g.SetClip(ClientRectangle);
                using(var p=Theme.Rounded(r,Height/2F))Theme.FillGradient(g,p,r,Color.FromArgb(47,145,245),Theme.Primary,0);
                g.Restore(state);
            }
            else
            {
                r.Width=(float)(Width*shown/100);
                if(r.Width>.1F)using(var p=Theme.Rounded(r,Height/2F))Theme.FillGradient(g,p,r,Color.FromArgb(47,145,245),Theme.Primary,0);
            }
        }
    }
    // 处理 WM_DPICHANGED 的窗体：把窗口拖到另一块 DPI 不同的显示器、或运行中改系统
    // 缩放时采用建议矩形，并按当前 DPI 从设计单位重新布局，避免反复乘比率的累计误差。
    // （docs/KNOWN-ISSUES.md：PerMonitorV2 下未处理 WM_DPICHANGED，原来只能重启程序）。
    // 运行在 system-DPI-aware 下时这个分支不会触发，等同于原来的行为。
    // Standard Windows non-client frame. The OS owns caption buttons, dragging,
    // hit testing, system menu, Snap, resizing and accessibility.
    internal class DpiAwareForm : Form
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int value, int size);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private float dpi = InitialDpi();
        private Icon shellIcon;
        private bool frameLayoutQueued;
        public float UiScale { get { return dpi / 96F; } }
        public event EventHandler UiDpiChanged;
        public event EventHandler UiLayoutSettled;
        public DpiAwareForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            ControlBox = true; ShowIcon = true;
            BackColor = Theme.Bg;
            Padding = Padding.Empty;
            try { shellIcon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);if(shellIcon!=null)Icon=shellIcon; } catch { }
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if(!IsHandleCreated || frameLayoutQueued || IsDisposed) return;
            frameLayoutQueued=true;
            BeginInvoke(new MethodInvoker(delegate {
                frameLayoutQueued=false;
                if(!IsDisposed){PerformLayout();if(UiLayoutSettled!=null)UiLayoutSettled(this,EventArgs.Empty);}
            }));
        }
        protected override void OnShown(EventArgs e)
        {
            if(StartPosition!=FormStartPosition.Manual)
            {
                Rectangle wa=Screen.FromHandle(Handle).WorkingArea;int gap=(int)Math.Round(16*UiScale);
                int width=Math.Min(Width,wa.Width-gap*2),height=Math.Min(Height,wa.Height-gap*2);
                if(width>0&&height>0)Bounds=new Rectangle(Math.Max(wa.Left+gap,Math.Min(Left,wa.Right-gap-width)),Math.Max(wa.Top+gap,Math.Min(Top,wa.Bottom-gap-height)),width,height);
            }
            base.OnShown(e);
        }
        protected override void Dispose(bool disposing) {base.Dispose(disposing);if(disposing&&shellIcon!=null){shellIcon.Dispose();shellIcon=null;}}
        private static float InitialDpi()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { return g.DpiX; } } catch { return 96; }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { uint value = GetDpiForWindow(Handle); if (value > 0) dpi = value; } catch { }
            ApplyCorners();
        }
        private void ApplyCorners() { try { int round = 2, caption = ColorTranslator.ToWin32(Theme.Bg), ink = ColorTranslator.ToWin32(Theme.Text); DwmSetWindowAttribute(Handle, 33, ref round, 4); DwmSetWindowAttribute(Handle, 35, ref caption, 4); DwmSetWindowAttribute(Handle, 36, ref ink, 4); } catch { } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0)
            {
                float next = (m.WParam.ToInt64() & 65535); if (next <= 0) next = dpi;
                dpi = next;
                MinimumSize=new Size((int)Math.Round(360*UiScale),(int)Math.Round(420*UiScale));
                ApplyUiScale(this, UiScale);
                if (m.LParam != IntPtr.Zero)
                {
                    RECT r=(RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam,typeof(RECT));
                    Bounds=new Rectangle(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top);
                }
                ApplyCorners(); if (UiDpiChanged != null) UiDpiChanged(this, EventArgs.Empty);
                PerformLayout(); Invalidate(true); m.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref m);
        }
        internal static void ApplyUiScale(Control root, float scale)
        {
            foreach (Control c in root.Controls)
            {
                ThemedControl themed=c as ThemedControl;
                if(themed!=null){themed.UiScale=scale;themed.Invalidate();}
                CardPanel card=c as CardPanel;
                if(card!=null && card.Padding!=Padding.Empty)card.Padding=new Padding((int)Math.Round(12*scale));
                RoundedField field=c as RoundedField;if(field!=null)field.UpdateInputLayout();
                ApplyUiScale(c,scale);
            }
        }
    }

    internal class AppleComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if(m.Msg!=15&&m.Msg!=0x318)return;
            if(Width<24||Height<10)return;
            using(Graphics g=m.Msg==0x318&&m.WParam!=IntPtr.Zero?Graphics.FromHdc(m.WParam):Graphics.FromHwnd(Handle))
            {
                float scale=g.DpiX/96F;float edge=2*scale;
                using(Pen border=new Pen(BackColor,edge*2))g.DrawRectangle(border,0,0,Width-1,Height-1);
                RectangleF arrow=new RectangleF(Width-22*scale,0,22*scale,Height);
                using(var b=new SolidBrush(BackColor))g.FillRectangle(b,arrow);
                g.SmoothingMode=SmoothingMode.AntiAlias;float x=arrow.Left+arrow.Width/2,y=Height/2F;
                using(Pen pen=new Pen(Enabled?Theme.Muted:Theme.Line,scale))g.DrawLines(pen,new PointF[]{new PointF(x-3*scale,y-1*scale),new PointF(x,y+2*scale),new PointF(x+3*scale,y-1*scale)});
            }
        }
    }
    internal class RoundedField : CardPanel
    {
        private readonly Control input;
        public RoundedField(Control control)
        {
            input=control; Radius=10; Fill=Theme.Surface; BackColor=Theme.Bg;
            TextBox text=input as TextBox; if(text!=null) { text.BorderStyle=BorderStyle.None; text.AutoSize=false; }
            ComboBox combo=input as ComboBox; if(combo!=null) {
                combo.FlatStyle=FlatStyle.Flat;combo.BackColor=Theme.Surface;combo.ForeColor=Theme.Text;
                combo.DrawMode=DrawMode.OwnerDrawFixed;
                combo.DrawItem+=delegate(object sender,DrawItemEventArgs e){e.DrawBackground();if(e.Index>=0)TextRenderer.DrawText(e.Graphics,combo.Items[e.Index].ToString(),combo.Font,e.Bounds,Theme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis);};
            }
            Controls.Add(input);
            input.GotFocus+=delegate {Border=Theme.Primary;Invalidate();}; input.LostFocus+=delegate {Border=Theme.Line;Invalidate();};
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);UpdateInputLayout();
        }
        internal void UpdateInputLayout()
        {
            if(input==null)return;
            int pad=(int)Math.Round(12*UiScale), h=input is ComboBox?input.PreferredSize.Height:(int)Math.Round(23*UiScale);
            input.SetBounds(pad,Math.Max(2,(Height-h)/2),Math.Max(1,Width-2*pad),h);
        }
    }
    internal class AppleListView : ListView
    {
        [System.Runtime.InteropServices.DllImport("uxtheme.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]private static extern int SetWindowTheme(IntPtr hwnd,string app,string id);
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);SetWindowTheme(Handle,"Explorer",null);}
        private ImageList rowImages;
        public AppleListView()
        {
            DoubleBuffered=true; BorderStyle=BorderStyle.None; OwnerDraw=true;
            BackColor=Theme.Surface; ForeColor=Theme.Text; Font=new Font(Theme.UiFont,9.5F);
            DrawColumnHeader+=delegate(object sender,DrawListViewColumnHeaderEventArgs e)
            {
                using(SolidBrush b=new SolidBrush(Theme.Surface))e.Graphics.FillRectangle(b,e.Bounds);
                TextRenderer.DrawText(e.Graphics,e.Header.Text,Theme.GetFont(8.5F,FontStyle.Regular,false),Rectangle.Inflate(e.Bounds,-10,0),Theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
                using(Pen p=new Pen(Theme.Line))e.Graphics.DrawLine(p,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
            };
            DrawItem+=delegate(object sender,DrawListViewItemEventArgs e) { if(View!=View.Details)e.DrawDefault=true; };
            DrawSubItem+=delegate(object sender,DrawListViewSubItemEventArgs e)
            {
                Color bg=e.Item.Selected?Theme.Bg:Theme.Surface;
                using(SolidBrush b=new SolidBrush(bg))e.Graphics.FillRectangle(b,e.Bounds);
                Rectangle rect=Rectangle.Inflate(e.Bounds,-10,0);
                if(e.ColumnIndex==0 && CheckBoxes)
                {
                    Point pos=new Point(e.Bounds.Left+4,e.Bounds.Top+(e.Bounds.Height-16)/2);
                    CheckBoxRenderer.DrawCheckBox(e.Graphics,pos,e.Item.Checked?System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal:System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
                    rect.X+=22;rect.Width=Math.Max(0,rect.Width-22);
                }
                Color ink=e.Item.Selected?Theme.Primary:(e.Item.ForeColor==Theme.Muted?Theme.Muted:Theme.Text2);
                TextRenderer.DrawText(e.Graphics,e.SubItem.Text,Font,rect,ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
                using(Pen p=new Pen(Theme.RowLine))e.Graphics.DrawLine(p,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
                if(Focused && e.Item.Focused && e.ColumnIndex==0)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(e.Item.Bounds,-1,-1));
            };
            SetRowScale(1);
        }
        public void SetRowScale(float scale)
        {
            ImageList old=rowImages; rowImages=new ImageList();rowImages.ImageSize=new Size(1,Math.Max(1,(int)Math.Round(36*scale)));SmallImageList=rowImages;if(old!=null)old.Dispose();
        }
        protected override void Dispose(bool disposing) { if(disposing&&rowImages!=null)rowImages.Dispose();base.Dispose(disposing); }
    }
    internal static class AppleUi
    {
        [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]private static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr wp,string text);
        public static void Cue(TextBox input,string text){input.AccessibleName=text;input.HandleCreated+=delegate{SendMessage(input.Handle,0x1501,(IntPtr)1,text);};if(input.IsHandleCreated)SendMessage(input.Handle,0x1501,(IntPtr)1,text);}
        public static RoundedField Field(Control input,Control parent,float scale)
        {
            RoundedField host=new RoundedField(input);host.UiScale=scale;parent.Controls.Add(host);return host;
        }
        public static CardPanel ListHost(ListView list,Control parent,float scale)
        {
            CardPanel host=new CardPanel();host.Radius=18;host.UiScale=scale;host.BackColor=Theme.Bg;
            host.Padding=new Padding((int)(12*scale));list.Dock=DockStyle.Fill;host.Controls.Add(list);parent.Controls.Add(host);return host;
        }
        public static Panel Content(Form form)
        {
            Panel panel=new Panel();panel.BackColor=Theme.Bg;panel.Dock=DockStyle.Fill;
            List<Control> items=new List<Control>();foreach(Control c in form.Controls)items.Add(c);
            foreach(Control c in items)panel.Controls.Add(c);form.Controls.Add(panel);return panel;
        }
    }

    internal static class Theme
    {
        // Quiet workshop palette: warm paper surfaces, deep evergreen ink, and a lime
        // signal reserved for the released-memory figure and progress ring.

        public static Color Bg, Surface, Card, Line, Track, Muted, Text2, Text;
        public static Color Coral, CoralDeep, Yellow, Violet, Mint, Sky, Emerald, EmeraldDot, Attention, BarAfter;
        public static Color Blue;                 // 明细数据条：克莱因蓝
        public static Color OnCoralInk, OkText;
        public static Color Ring1, Ring2, Ring3;
        public static Color HeroBg, HeroInk, HeroSoft, HeroLine, HeroPlate;

        // v1.1.5-dev 新增令牌
        public static Color HeroTop, HeroMid, HeroBottom;    // 首卡克莱因蓝渐变
        public static Color HeroTrack;                       // 首卡环形轨道（半透明白）
        public static Color BarBeforeTop, BarBeforeBottom;   // 对比柱「清理前」竖向渐变
        public static Color RowLine, HeadLine;               // 表格行线 / 表头线
        public static Color Success, WarnInk, DangerInk, DangerDot, Muted2;
        public static Color SecondaryLine;                   // 次要按钮描边
        public static Color ProcessCard, ProcessLine, ChipBg, ChipLine;
        private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();

        public static void Apply()
        {
            Bg = Color.FromArgb(0xF5, 0xF5, 0xF7);
            Surface = Color.White;
            Card = Surface;
            Line = Color.FromArgb(0xDE, 0xDE, 0xE3);
            Track = Color.FromArgb(0xE8, 0xE8, 0xED);
            Muted = Color.FromArgb(0x6E, 0x6E, 0x73);
            Muted2 = Color.FromArgb(0x86, 0x86, 0x8B);
            Text2 = Color.FromArgb(0x42, 0x42, 0x45);
            Text = Color.FromArgb(0x1D, 0x1D, 0x1F);
            HeroTop = Color.FromArgb(0xFF, 0xFF, 0xFF);
            HeroMid = Color.FromArgb(0xFA, 0xFA, 0xFC);
            HeroBottom = Color.FromArgb(0xF5, 0xF5, 0xF7);
            HeroSoft = Color.FromArgb(217, 0xFF, 0xFF, 0xFF);
            HeroLine = Color.FromArgb(56, 0xFF, 0xFF, 0xFF);
            HeroTrack = Color.FromArgb(66, 0xFF, 0xFF, 0xFF);
            HeroPlate = Color.FromArgb(0, 0, 0, 0);
            BarBeforeTop = Color.FromArgb(0xBF, 0xE3, 0xFF);
            BarBeforeBottom = Color.FromArgb(0x8F, 0xC7, 0xFF);
            BarAfter = Color.FromArgb(107, 0xFF, 0xFF, 0xFF);
            RowLine = Color.FromArgb(18, 0, 0, 0);
            HeadLine = Color.FromArgb(28, 0, 0, 0);
            Success = Color.FromArgb(0x30, 0xB6, 0x50);
            OkText = Color.FromArgb(0x24, 0x81, 0x3B);
            WarnInk = Color.FromArgb(0x9E, 0x64, 0x00);
            DangerInk = Color.FromArgb(0xBD, 0x30, 0x2B);
            DangerDot = Color.FromArgb(0xD8, 0x3B, 0x36);
            SecondaryLine = Color.FromArgb(0xD2, 0xD2, 0xD7);
            ProcessCard = Surface;
            ProcessLine = Line;
            ChipBg = Color.FromArgb(26, 0x22, 0xC5, 0x5E);
            ChipLine = Color.FromArgb(82, 0x22, 0xC5, 0x5E);

            Ring1 = Color.White;
            Ring2 = Ring3 = Ring1;
            Coral = Color.FromArgb(0x00, 0x6C, 0xDE);
            CoralDeep = Color.FromArgb(0x00, 0x59, 0xBB);
            Blue = Coral;
            Yellow = Color.FromArgb(0x9E, 0x64, 0x00);
            Violet = Color.FromArgb(0x65, 0x5A, 0xB8);
            Mint = BarBeforeTop;
            Sky = BarBeforeBottom;
            Emerald = Success;
            EmeraldDot = Success;
            Attention = Color.FromArgb(0x9E, 0x64, 0x00);
            OnCoralInk = Color.White;
            HeroInk = Color.White;
            HeroBg = HeroMid;
            Primary = Coral;
            PrimaryHover = CoralDeep;
            OnPrimary = OnCoralInk;
            Ok = OkText;
            Warn = WarnInk;
            Danger = DangerInk;
            OkFill = Success;
            WarnFill = Color.FromArgb(0xF5, 0x9E, 0x0B);
            DangerFill = DangerDot;
        }

        public static Color Primary, PrimaryHover, OnPrimary, Ok, Warn, Danger, OkFill, WarnFill, DangerFill;

        // Paint methods share a small point-size cache so repeated invalidations
        // never allocate GDI fonts. Point sizes remain correct across DPI changes.
        public static Font GetFont(float size, FontStyle style, bool numeric)
        {
            string family = numeric ? NumFont : UiFont;
            string key = family + "|" + size.ToString("R", CultureInfo.InvariantCulture) + "|" + (int)style;
            lock (FontCache)
            {
                Font font;
                if (!FontCache.TryGetValue(key, out font))
                {
                    font = new Font(family, size, style, GraphicsUnit.Point);
                    FontCache.Add(key, font);
                }
                return font;
            }
        }

        static Theme() { Apply(); }

        // 设计字号（pt）：标题 12 / 正文 10 / 列头 9 / 数字 10.5 —— 与预览稿一致。
        public const float FontTitle = 12F;
        public const float FontBody = 10.5F;
        public const float FontHead = 9F;
        public const float FontNum = 10.5F;

        public const string UiFont = "Microsoft YaHei UI";
        public const string NumFont = "Segoe UI";

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { return p; }
            radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2F));
            float d = radius * 2f;
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 线性渐变填充。angle 用 GDI+ 约定（90 = 自下而上，180 = 自右向左）；
        // CSS 的 140deg 视觉效果 = 从左上到右下，对应这里约 135°。
        public static void FillGradient(Graphics g, GraphicsPath path, RectangleF r, Color from, Color to, float angle)
        {
            if (r.Width <= 0.5F || r.Height <= 0.5F) { return; }
            using (LinearGradientBrush b = new LinearGradientBrush(r, from, to, angle))
            {
                g.FillPath(b, path);
            }
        }

        // 三停渐变：首卡是「亮蓝 → 主蓝 55% → 主蓝」，比两停更接近预览的层次。
        public static void FillGradient3(Graphics g, GraphicsPath path, RectangleF r,
                                         Color from, Color to, float midPos, float angle)
        {
            if (r.Width <= 0.5F || r.Height <= 0.5F) { return; }
            using (LinearGradientBrush b = new LinearGradientBrush(r, from, to, angle))
            {
                ColorBlend blend = new ColorBlend(3);
                blend.Colors = new Color[] { from, to, to };
                blend.Positions = new float[] { 0F, Math.Max(0.01F, Math.Min(0.99F, midPos)), 1F };
                b.InterpolationColors = blend;
                g.FillPath(b, path);
            }
        }

        public static Color Blend(Color a, Color b, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // 首卡的 1px 内高光：GDI+ 没法做高斯模糊，外投影在自绘控件里会被
        // 控件边界裁掉，所以用内高光 + 渐变本身表达"卡片浮起"的层次（预览稿里的
        // 0 12px 30px rgba(0,47,167,.18) 在 1180px 宽下本来就只有 8% 可见度）。
        public static void DrawTopHighlight(Graphics g, RectangleF r, float radius, Color tint)
        {
            RectangleF top = new RectangleF(r.X + radius * 0.4F, r.Y + 1F, r.Width - radius * 0.8F, 1F);
            using (Pen p = new Pen(tint, 1F))
            {
                g.DrawLine(p, top.Left, top.Top, top.Right, top.Top);
            }
        }
    }

    // Base class: double-buffered painting + a design-unit scaler so that hand
    // written offsets follow the monitor DPI the same way control bounds do.
    internal class ThemedControl : Control
    {
        public float UiScale = 1F;

        // 窗口换到另一块 DPI 不同的显示器时，手写的设计单位偏移也要跟着变，
        // 否则自绘内容会和被 ScaleTree 缩放过的控件边界错位。
        public void RescaleBy(float factor)
        {
            if (factor <= 0F) { return; }
            UiScale *= factor;
            Invalidate();
        }

        public ThemedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected int S(float v) { return (int)Math.Round(v * UiScale); }
        protected float SF(float v) { return v * UiScale; }
    }

    // Rounded surface with a hairline border (cards 16, popups 20).
    internal class CardPanel : ThemedControl
    {
        public override Rectangle DisplayRectangle { get { return new Rectangle(Padding.Left,Padding.Top,Math.Max(0,Width-Padding.Horizontal),Math.Max(0,Height-Padding.Vertical)); } }
        public float Radius = 12F;
        public Color Fill = Theme.Surface;
        public Color Border = Theme.Line;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            RectangleF r = new RectangleF(SF(0.5F), SF(0.5F), Width - SF(1F), Height - SF(1F));
            using (GraphicsPath p = Theme.Rounded(r, SF(Radius)))
            using (SolidBrush b = new SolidBrush(Fill))
            using (Pen pen = new Pen(Border, SF(1F)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }
    }

    // Buttons with the six required states: default / hover / focus / disabled /
    // busy (loading) / error(= danger tint). Keyboard: Space or Enter activates.
    internal class FlatButton : ThemedControl
    {
        public enum Style { Primary, Secondary, Ghost, Warn }

        public Style Kind = Style.Secondary;
        public string BusyText = "正在处理…";
        public bool Danger = false;
        public bool Busy = false;
        private bool hover;
        private bool down;
        private bool focused;
        private readonly Timer feedbackTimer=new Timer();
        private readonly System.Diagnostics.Stopwatch feedbackClock=System.Diagnostics.Stopwatch.StartNew();
        private double feedback,feedbackFrom,feedbackTarget,feedbackChanged;
        private void Feedback(){feedbackFrom=feedback;feedbackTarget=hover||down?1:0;feedbackChanged=feedbackClock.Elapsed.TotalMilliseconds;if(Program.AnimationsEnabled())feedbackTimer.Start();else{feedback=feedbackTarget;feedbackTimer.Stop();}}
        protected override void Dispose(bool disposing){if(disposing)feedbackTimer.Dispose();base.Dispose(disposing);}

        public FlatButton()
        {
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            feedbackTimer.Interval=15;feedbackTimer.Tick+=delegate{double t=(feedbackClock.Elapsed.TotalMilliseconds-feedbackChanged)/120;feedback=feedbackFrom+(feedbackTarget-feedbackFrom)*MotionCurve.Ease(t);Invalidate();if(t>=1)feedbackTimer.Stop();};
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ButtonAccessible(this); }
        private class ButtonAccessible : ControlAccessibleObject
        {
            private readonly FlatButton button;
            public ButtonAccessible(FlatButton b) : base(b) {button=b;}
            public override string Name {get{return button.Busy?button.BusyText:button.Text;}set{}}
            public override AccessibleRole Role {get{return AccessibleRole.PushButton;}}
            public override string DefaultAction {get{return "按下";}}
            public override void DoDefaultAction() {if(button.Enabled&&!button.Busy)button.OnClick(EventArgs.Empty);}
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Feedback();Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Feedback();Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { focused = true; Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { focused = false; Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter) { return true; }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                down = true;
                Invalidate();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                down = false;
                Invalidate();
                if (Enabled && !Busy) { OnClick(EventArgs.Empty); }
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            RectangleF r = new RectangleF(SF(0.5F), SF(0.5F), Width - SF(1F), Height - SF(1F));
            Color fill;
            Color border;
            Color ink;

            if (!Enabled || Busy)
            {
                fill = Theme.Surface;
                border = Theme.Line;
                ink = Theme.Muted;
            }
            else if (Kind == Style.Primary)
            {
                fill = Danger ? Theme.DangerFill : (down ? Theme.CoralDeep : Theme.Blend(Theme.Primary,Theme.PrimaryHover,feedback));
                border = fill;
                ink = Theme.OnPrimary;                // 白字 on #002FA7
            }
            else if (Kind == Style.Secondary)
            {
                fill = Theme.Blend(Theme.Surface,Theme.Bg,feedback);
                border = Theme.SecondaryLine;
                ink = Theme.Text;
            }
            else if (Kind == Style.Warn)
            {
                fill = hover || down ? ControlPaint.Dark(Theme.WarnFill, 0.04F) : Theme.WarnFill;
                border = fill;
                ink = Color.FromArgb(0x3A, 0x24, 0x00);
            }
            else
            {
                fill = (hover || down) ? Theme.Surface : Color.Empty;
                border = Color.Empty;
                ink = hover ? Theme.Text2 : Theme.Muted;
            }

            using (GraphicsPath p = Theme.Rounded(r, SF(8F)))
            {
                if (fill != Color.Empty)
                {
                    using (SolidBrush b = new SolidBrush(fill)) { g.FillPath(b, p); }
                }
                if (border != Color.Empty)
                {
                    using (Pen pen = new Pen(border, SF(1F))) { g.DrawPath(pen, p); }
                }
                if (focused && Enabled)
                {
                    RectangleF ring = new RectangleF(r.X + SF(2F), r.Y + SF(2F), r.Width - SF(4F), r.Height - SF(4F));
                    using (GraphicsPath rp = Theme.Rounded(ring, SF(7F)))
                    using (Pen pen = new Pen(Color.FromArgb(135,Theme.PrimaryHover), SF(1F)))
                    {
                        g.DrawPath(pen, rp);
                    }
                }
            }

            string text = Busy ? BusyText : Text;
            Font f = Theme.GetFont(10.5F, FontStyle.Regular, false);
            using (SolidBrush b = new SolidBrush(ink))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, f, b, new RectangleF(0, 0, Width, Height), sf);
            }
        }
    }

    internal class MemRow
    {
        public string Label = "";
        public string Before = "";
        public string After = "";
        public string Delta = "";
        public Color DeltaColor = Theme.Muted;
        public Color TickColor = Theme.Primary;
    }

    // Memory detail table: title + header + rows, 1px separators, tabular numbers.
    internal class MemTable : ThemedControl
    {
        public float Radius = 12F;
        public string Title = "内存明细";
        public string ColBefore = "清理前";
        public string ColAfter = "清理后";
        public string ColDelta = "变化";
        public List<MemRow> Rows = new List<MemRow>();
        public bool Narrow = false;
        public bool Micro = false;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(BackColor);
            using(GraphicsPath p=Theme.Rounded(new RectangleF(SF(.5F),SF(.5F),Width-SF(1),Height-SF(1)),SF(12)))
            using(SolidBrush b=new SolidBrush(Theme.Surface))using(Pen pen=new Pen(Theme.Line,SF(1))){g.FillPath(b,p);g.DrawPath(pen,p);}
            float pad=SF(20),w=Width-pad*2;bool small=Width<SF(560);float y=SF(20);
            using(SolidBrush ink=new SolidBrush(Theme.Text2))using(SolidBrush muted=new SolidBrush(Theme.Muted))using(Pen line=new Pen(Theme.RowLine,SF(1)))
            using(StringFormat trim=new StringFormat())using(StringFormat right=new StringFormat())
            {
                trim.Trimming=StringTrimming.EllipsisCharacter;trim.FormatFlags=StringFormatFlags.NoWrap;
                right.Alignment=StringAlignment.Far;right.Trimming=StringTrimming.EllipsisCharacter;
                g.DrawString(Title,Theme.GetFont(12,FontStyle.Bold,false),ink,pad,y);y+=SF(34);
                if(!small)
                {
                    float before=pad+w*(Narrow?.72F:.55F),after=pad+w*.79F;
                    g.DrawString(Narrow?"清理前 → 后":"清理前",Theme.GetFont(8.5F,FontStyle.Regular,false),muted,new RectangleF(before-SF(Narrow?210:130),y,SF(Narrow?210:130),SF(20)),right);
                    if(!Narrow)g.DrawString(ColAfter,Theme.GetFont(8.5F,FontStyle.Regular,false),muted,new RectangleF(after-SF(130),y,SF(130),SF(20)),right);
                    g.DrawString(ColDelta,Theme.GetFont(8.5F,FontStyle.Regular,false),muted,new RectangleF(Width-pad-SF(110),y,SF(110),SF(20)),right);y+=SF(26);
                }
                foreach(MemRow row in Rows)
                {
                    float height=SF(small?68:36);
                    g.DrawLine(line,pad,y,Width-pad,y);
                    g.DrawString(row.Label,Theme.GetFont(small?9:9.5F,FontStyle.Regular,false),ink,new RectangleF(pad,y+SF(10),small?w:w*.34F,SF(22)),trim);
                    using(SolidBrush delta=new SolidBrush(row.DeltaColor))
                    {
                        if(small)
                        {
                            g.DrawString(Narrow?(row.Before.Replace(" GB / ","/").Replace(" MiB / ","/")+" → "+row.After.Replace(" GB / ","/").Replace(" MiB / ","/")):row.Before,Theme.GetFont(8.5F,FontStyle.Regular,true),muted,new RectangleF(pad,y+SF(32),Math.Max(SF(100),w-SF(90)),SF(33)));
                            g.DrawString(row.Delta,Theme.GetFont(9F,FontStyle.Regular,true),delta,new RectangleF(Width-pad-SF(88),y+SF(32),SF(88),SF(24)),right);
                        }
                        else
                        {
                            float before=pad+w*(Narrow?.72F:.55F),after=pad+w*.79F;
                            g.DrawString(Narrow?(row.Before.Replace(" GB / ","/").Replace(" MiB / ","/")+" → "+row.After.Replace(" GB / ","/").Replace(" MiB / ","/")):row.Before,Theme.GetFont(9F,FontStyle.Regular,true),ink,new RectangleF(before-SF(Narrow?210:160),y+SF(10),SF(Narrow?210:160),SF(24)),right);
                            if(!Narrow)g.DrawString(row.After,Theme.GetFont(9F,FontStyle.Regular,true),ink,new RectangleF(after-SF(160),y+SF(10),SF(160),SF(24)),right);
                            g.DrawString(row.Delta,Theme.GetFont(9F,FontStyle.Regular,true),delta,new RectangleF(Width-pad-SF(110),y+SF(10),SF(110),SF(24)),right);
                        }
                    }
                    y+=height;
                }
            }
        }
    }

    internal class ProcRow
    {
        public string State = "";
        public string Name = "";
        public string Memory = "";
        public string Path = "";
        public string IconPath;
        public Color StateColor = Theme.Muted;
        public bool IsProblem;
        public bool Keepable = false;
        public bool Kept = false;
        public bool CanKeepNextTime;
        public bool KeptNextTime;
        public bool CanUndoKeep;
    }

    // Process list: hand painted, keyboard scrollable, with a restrained paper
    // header and compact state markers. The logo uses the evergreen hero token.
    internal class LogoBox : ThemedControl
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            RectangleF r = new RectangleF(SF(0.5F), SF(0.5F), Width - SF(1F), Height - SF(1F));
            using (GraphicsPath p = Theme.Rounded(r, SF(12F)))
            {
                Theme.FillGradient3(g, p, r, Theme.HeroTop, Theme.HeroBottom, 0.55F, 135F);
            }
            using (Pen pen = new Pen(Color.White, Math.Max(SF(1.6F), Width * 0.075F)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float cx = Width / 2F;
                float cy = Height / 2F + SF(1F);
                float rad = Width * 0.27F;
                g.DrawArc(pen, cx - rad, cy - rad, rad * 2F, rad * 2F, 300F, 300F);
                g.DrawLine(pen, cx, cy - rad - SF(4F), cx, cy - rad + SF(4F));
            }
        }
    }

    // Status chip: success=green, warning=ochre, busy=evergreen.
    internal class StatusChip : ThemedControl
    {
        public string LabelText = "网络自检正常";
        public bool IsOk = true;
        public bool Busy = false;
        public bool Neutral = false;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            RectangleF r = new RectangleF(SF(0.5F), SF(0.5F), Width - SF(1F), Height - SF(1F));
            Color dot;
            Color bg;
            Color line;
            Color ink;
            if (Busy)
            {
                dot = Theme.Primary;
                bg = Color.FromArgb(26, Theme.Primary);
                line = Color.FromArgb(82, Theme.Primary);
                ink = Theme.Primary;
            }
            else if (Neutral)
            {
                dot = Theme.Muted;
                bg = Theme.Bg;
                line = Theme.Line;
                ink = Theme.Text2;
            }
            else if (IsOk)
            {
                dot = Theme.EmeraldDot;
                bg = Theme.ChipBg;
                line = Theme.ChipLine;
                ink = Theme.OkText;
            }
            else
            {
                dot = Theme.Attention;
                bg = Color.FromArgb(34, Theme.WarnFill);
                line = Color.FromArgb(92, Theme.WarnFill);
                ink = Theme.WarnInk;
            }
            using (GraphicsPath p = Theme.Rounded(r, SF(999F)))
            using (SolidBrush b = new SolidBrush(bg))
            using (Pen pen = new Pen(line, SF(1F)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            float dy = Height / 2F;
            using (SolidBrush db = new SolidBrush(dot))
            {
                g.FillEllipse(db, SF(10F), dy - SF(4F), SF(8F), SF(8F));
            }
            Font f = Theme.GetFont(9F, FontStyle.Regular, false);
            using (SolidBrush tb = new SolidBrush(ink))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(LabelText, f, tb, new RectangleF(SF(24F), 0, Width - SF(32F), Height), sf);
            }
        }
    }

    // 首卡：全页唯一大面积高饱和块 —— 环形进度 + 英雄数值 + 释放前后对比柱
    internal class HeroCard : ThemedControl
    {
        public double UsagePct = 42;
        public string AvailText = "可用 8.79 GB";
        public string FreedLabel = "物理内存已用变化";
        public string HeroText = "0.00";
        public string HeroUnit = "GB";
        public string NoteText = "";
        public string SummaryText = "";
        public string CaptionText = "";

        // 清理中（live）态：底部换成「当前动作 + 细粒度进度条 + 一行统计」
        public bool Live = false;
        public string ActionText = "";
        public double ActionPct = 0;
        public string LiveStats = "";
        public bool Indeterminate, Preparing;
        internal readonly ProgressTrack Progress=new ProgressTrack();
        public HeroCard(){Controls.Add(Progress);Progress.Visible=false;}
        protected override void OnResize(EventArgs e){base.OnResize(e);int pad=S(Width<SF(560)?20:28);Progress.Bounds=new Rectangle(pad,Height-S(Height<SF(128)?12:19),Math.Max(1,Width-pad*2),S(5));}


        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(BackColor);
            RectangleF card=new RectangleF(SF(.5F),SF(.5F),Width-SF(1),Height-SF(1));
            using(GraphicsPath p=Theme.Rounded(card,SF(14)))
            using(SolidBrush b=new SolidBrush(Theme.Surface))
            using(Pen pen=new Pen(Theme.Line,SF(1))) {g.FillPath(b,p);g.DrawPath(pen,p);}
            if(Height<SF(128)){
                float shortPad=SF(16);
                using(var b=new SolidBrush(Theme.Text2))g.DrawString(FreedLabel,Theme.GetFont(9,FontStyle.Regular,false),b,shortPad,SF(8));
                using(var b=new SolidBrush(Theme.Primary))g.DrawString(HeroText+(Preparing?"":" "+HeroUnit),Theme.GetFont(28,FontStyle.Bold,true),b,shortPad,SF(22));
                using(var b=new SolidBrush(Theme.Muted))using(var trim=new StringFormat()){trim.Trimming=StringTrimming.EllipsisCharacter;trim.FormatFlags=StringFormatFlags.NoWrap;g.DrawString(Live?ActionText:SummaryText,Theme.GetFont(9,FontStyle.Regular,false),b,new RectangleF(shortPad,Height-SF(Live?34:22),Width-shortPad*2,SF(20)),trim);}
                return;
            }
            bool small=Width<SF(560); float pad=SF(small?20:28), top=SF(18);
            using(StringFormat trim=new StringFormat())
            {
                trim.FormatFlags=StringFormatFlags.NoWrap;trim.Trimming=StringTrimming.EllipsisCharacter;
                using(SolidBrush b=new SolidBrush(Theme.Text2)) g.DrawString(FreedLabel,Theme.GetFont(9.5F,FontStyle.Regular,false),b,pad,top);
                using(SolidBrush b=new SolidBrush(Theme.Primary))
                    g.DrawString(HeroText+(Preparing?"":" "+HeroUnit),Theme.GetFont(small?28F:34F,FontStyle.Bold,true),b,new RectangleF(pad,top+SF(22),Width-pad*2-SF(small?64:220),SF(62)),trim);
                if(!small)
                {
                    using(SolidBrush b=new SolidBrush(Theme.Text2)) g.DrawString(Live?LiveStats:SummaryText,Theme.GetFont(9F,FontStyle.Regular,false),b,new RectangleF(pad,top+SF(83),Width-pad*2-SF(180),SF(22)),trim);
                }
                if(!Live){
                float ring=SF(small?56:68), x=Width-pad-ring, y=SF(small?28:24);
                RectangleF rr=new RectangleF(x+SF(4),y+SF(4),ring-SF(8),ring-SF(8));
                using(Pen pen=new Pen(Theme.Track,SF(5))) g.DrawEllipse(pen,rr);
                using(Pen pen=new Pen(Theme.Primary,SF(5))) {pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;if(!Preparing)g.DrawArc(pen,rr,-90,(float)Math.Max(.1,Math.Min(100,UsagePct)*3.6));}
                using(StringFormat center=new StringFormat())
                using(SolidBrush b=new SolidBrush(Theme.Text)) {center.Alignment=StringAlignment.Center;center.LineAlignment=StringAlignment.Center;g.DrawString(Preparing?"—":Math.Round(UsagePct)+"%",Theme.GetFont(11,FontStyle.Bold,true),b,new RectangleF(x,y,ring,ring),center);}
                using(SolidBrush b=new SolidBrush(Theme.Muted)) g.DrawString("内存占用",Theme.GetFont(9,FontStyle.Regular,false),b,new RectangleF(x-SF(8),y+ring+SF(8),ring+SF(24),SF(18)),trim);
                }else{
                    using(var b=new SolidBrush(Theme.Muted))g.DrawString("内存占用 "+(Preparing?"—":UsagePct.ToString("0")+"%"),Theme.GetFont(9,FontStyle.Regular,false),b,Width-pad-SF(110),SF(22));
                }
                if(Live)
                {
                    float ty=Height-SF(36);
                    using(SolidBrush b=new SolidBrush(Theme.Text2))g.DrawString(ActionText,Theme.GetFont(9,FontStyle.Regular,false),b,new RectangleF(pad,ty-SF(16),Width-pad*2,SF(19)),trim);
                    // ProgressTrack paints separately; the animation never invalidates the whole form.

                }
                else {
                    if(small)using(SolidBrush b=new SolidBrush(Theme.Muted))g.DrawString(SummaryText,Theme.GetFont(9F,FontStyle.Regular,false),b,new RectangleF(pad,Height-SF(44),Width-pad*2,SF(20)),trim);
                    using(SolidBrush b=new SolidBrush(Theme.Muted))g.DrawString(CaptionText,Theme.GetFont(9,FontStyle.Regular,false),b,new RectangleF(pad,Height-SF(23),Width-pad*2,SF(20)),trim);
                }
            }
        }
    }

    internal class ProcList : ThemedControl
    {
        public List<ProcRow> Rows = new List<ProcRow>();
        public bool ShowPath = true;
        public bool Selectable = false;
        public event EventHandler KeptChanged;
        public string Title = "已结束的进程";
        public string Subtitle = "";
        public int Offset = 0;

        public int RowHeight = 32;
        private readonly int headH = 30;
        private int titleH {get{return Height<SF(240)?40:56;}}

        private const int KeepActionWidth = 78;
        private static readonly Color AlternateRow = Color.FromArgb(249,249,251);
        private readonly Dictionary<int, Button> keepActions = new Dictionary<int, Button>();
        private readonly ToolTip rowTip = new ToolTip();
        private readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Bitmap genericIcon = SystemIcons.Application.ToBitmap();
        private int tipRow = -1;
        public ProcList()
        {
            TabStop = true;
            SetStyle(ControlStyles.Selectable, true);
            MouseMove += delegate(object sender, MouseEventArgs e) {
                int index = Offset + (e.Y - S(titleH + headH)) / Math.Max(1,S(RowHeight));
                if(e.Y < S(titleH+headH))index=-1;
                if (index == tipRow) return;
                tipRow = index;
                string tip = "";
                if (index >= 0 && index < Rows.Count)
                {
                    ProcRow row = Rows[index];
                    tip = row.Name + "\n" + row.Path;
                    if (keepActions.Count > 0 && Width < SF(430) && row.IconPath != null)
                        tip += "\n" + row.Memory + " MB";
                }
                rowTip.SetToolTip(this, tip);
            };
            Disposed += delegate
            {
                rowTip.Dispose();
                genericIcon.Dispose();
                foreach (Bitmap image in icons.Values) if (image != null) image.Dispose();
                icons.Clear();
            };
        }

        public void SetKeepActions(Action<ProcRow> activate)
        {
            SuspendLayout();
            foreach (Button action in keepActions.Values) action.Dispose();
            keepActions.Clear();
            for (int i = 0; i < Rows.Count; i++)
            {
                ProcRow row = Rows[i];
                if (!row.CanKeepNextTime) continue;

                var action = new Button
                {
                    Name = "KeepResultRow" + i, TabIndex = i, Visible = false,
                    FlatStyle = FlatStyle.Flat, Font = Theme.GetFont(9, FontStyle.Regular, false),
                    BackColor = i % 2 == 0 ? Theme.Surface : AlternateRow,
                    ForeColor = Theme.Primary, UseVisualStyleBackColor = false, UseMnemonic = false,
                    Margin = Padding.Empty
                };
                action.FlatAppearance.BorderSize = 0;
                action.FlatAppearance.MouseOverBackColor = Theme.Track;
                rowTip.SetToolTip(action, row.IconPath);
                action.Click += delegate { activate(row); };
                keepActions.Add(i, action);
                Controls.Add(action);
            }
            RefreshKeepActions();
            ResumeLayout();
        }

        public void RefreshKeepActions()
        {
            int bottom = Offset + VisibleRows();
            foreach (var item in keepActions)
            {
                int index = item.Key;
                Button action = item.Value;
                bool visible = index >= Offset && index < bottom;
                if (!visible && action.Focused) Focus();
                action.Visible = visible;
                if (!visible) continue;
                ProcRow row = Rows[index];
                action.Text = row.CanUndoKeep ? "撤销保留" : row.KeptNextTime ? "已保留" : "下次保留";
                action.AccessibleName = action.Text + " · " + row.Name + " · " + row.IconPath;
                action.Enabled = row.CanUndoKeep || !row.KeptNextTime;
                action.SetBounds(Width - S(20 + KeepActionWidth),
                    S(titleH + headH + (index - Offset) * RowHeight + 3), S(KeepActionWidth), S(RowHeight - 6));
            }
        }

        private Bitmap RowIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return genericIcon;
            Bitmap icon;
            if (!icons.TryGetValue(path, out icon))
            {
                icons.Add(path, null);
                LoadRowIcon(path);
            }
            return icon ?? genericIcon;
        }

        private void LoadRowIcon(string path)
        {
            Task.Run(delegate
            {
                Bitmap icon = KeepPage.LocalIcon(path);
                if (icon == null) return;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed) { icon.Dispose(); return; }
                        icons[path] = icon;
                        Invalidate();
                    }));
                }
                catch (InvalidOperationException) { icon.Dispose(); }
            });
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollBy(-Math.Sign(e.Delta));
            base.OnMouseWheel(e);
        }

        // 预览时点一下整行即可勾选"这个进程要保留"，勾完由调用方写回 keep.json。
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Selectable) { return; }
            float top = SF(titleH + headH);
            if (e.Y < top) { return; }
            int idx = Offset + (int)((e.Y - top) / SF(RowHeight));
            if (idx < 0 || idx >= Rows.Count) { return; }
            ProcRow row = Rows[idx];
            if (!row.Keepable) { return; }
            row.Kept = !row.Kept;
            Invalidate();
            if (KeptChanged != null) { KeptChanged(this, EventArgs.Empty); }
        }

        private void DrawCheck(Graphics g, float x, float y, bool on)
        {
            float s = SF(15F);
            using (GraphicsPath p = Theme.Rounded(new RectangleF(x, y, s, s), SF(4F)))
            {
                if (on)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Primary)) { g.FillPath(b, p); }
                    using (Pen pen = new Pen(Color.White, SF(1.8F)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(x + s * 0.24F, y + s * 0.54F),
                            new PointF(x + s * 0.44F, y + s * 0.74F),
                            new PointF(x + s * 0.78F, y + s * 0.30F)
                        });
                    }
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Theme.Surface)) { g.FillPath(b, p); }
                    using (Pen pen = new Pen(Theme.Line, SF(1.2F))) { g.DrawPath(pen, p); }
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int page = Math.Max(1, (Height - S(titleH) - S(headH)) / S(RowHeight) - 1);
            switch (e.KeyCode)
            {
                case Keys.Down: ScrollBy(1); e.Handled = true; break;
                case Keys.Up: ScrollBy(-1); e.Handled = true; break;
                case Keys.PageDown: ScrollBy(page); e.Handled = true; break;
                case Keys.PageUp: ScrollBy(-page); e.Handled = true; break;
                case Keys.Home: ScrollBy(-Offset); e.Handled = true; break;
                case Keys.End: ScrollBy(Rows.Count); e.Handled = true; break;
            }
            base.OnKeyDown(e);
        }

        private int VisibleRows()
        {
            int h = Height - S(titleH) - S(headH);
            return Math.Max(1, h / S(RowHeight));
        }

        private void ScrollBy(int rows)
        {
            int max = Math.Max(0, Rows.Count - VisibleRows());
            Offset = Math.Max(0, Math.Min(max, Offset + rows));
            RefreshKeepActions();
            Invalidate();
        }

        public void InvalidateHeader()
        {
            Invalidate(new Rectangle(0, 0, Width, S(titleH + headH + 4)));
        }

        public void InvalidateRow(ProcRow row)
        {
            int index = Rows.IndexOf(row);
            if (index < Offset || index >= Offset + VisibleRows()) { return; }
            int top = S(titleH + headH + (index - Offset) * RowHeight);
            Invalidate(new Rectangle(0, top, Width, S(RowHeight + 2)));
        }

        protected override void OnResize(EventArgs e)
        {
            int max = Math.Max(0, Rows.Count - VisibleRows());
            if (Offset > max) { Offset = max; }
            RefreshKeepActions();
            base.OnResize(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(BackColor);
            using(GraphicsPath p=Theme.Rounded(new RectangleF(SF(.5F),SF(.5F),Width-SF(1),Height-SF(1)),SF(12)))
            using(SolidBrush b=new SolidBrush(Theme.Surface))using(Pen pen=new Pen(Theme.Line,SF(1))){g.FillPath(b,p);g.DrawPath(pen,p);}
            float pad=SF(20),wide=Width-pad*2;
            bool showActions=keepActions.Count>0;
            bool showMemory=!showActions||Width>=SF(430);
            float actionX=Width-pad-SF(KeepActionWidth);
            float contentRight=showActions?actionX-SF(8):Width-pad;
            using(StringFormat clip=new StringFormat())using(StringFormat right=new StringFormat())
            using(SolidBrush ink=new SolidBrush(Theme.Text2))using(SolidBrush muted=new SolidBrush(Theme.Muted))
            using(Pen line=new Pen(Theme.RowLine,SF(1)))
            {
                clip.Trimming=StringTrimming.EllipsisCharacter;clip.FormatFlags=StringFormatFlags.NoWrap;right.Alignment=StringAlignment.Far;right.FormatFlags=StringFormatFlags.NoWrap;
                g.DrawString(Title,Theme.GetFont(12,FontStyle.Bold,false),ink,new RectangleF(pad,SF(12),wide,SF(24)),clip);
                if(titleH>40)g.DrawString(Subtitle,Theme.GetFont(9F,FontStyle.Regular,false),muted,new RectangleF(pad,SF(36),wide,SF(18)),clip);
                float cy=SF(titleH),nameX=pad+SF(Selectable?106:showMemory?82:60),memX=ShowPath?Width*.49F:contentRight;
                float pathX=Width*.53F;
                g.DrawString("状态",Theme.GetFont(8.5F,FontStyle.Regular,false),muted,pad,cy);
                g.DrawString("应用 / 进程",Theme.GetFont(8.5F,FontStyle.Regular,false),muted,nameX,cy);
                if(showMemory)g.DrawString("内存 MB",Theme.GetFont(8.5F,FontStyle.Regular,false),muted,new RectangleF(memX-SF(82),cy,SF(82),SF(20)),right);
                if(ShowPath)g.DrawString("路径 / 说明",Theme.GetFont(8.5F,FontStyle.Regular,false),muted,pathX,cy);
                if(showActions)
                {
                    using(var center=new StringFormat { Alignment=StringAlignment.Center })
                        g.DrawString("白名单",Theme.GetFont(8.5F,FontStyle.Regular,false),muted,new RectangleF(actionX,cy,SF(KeepActionWidth),SF(20)),center);
                }
                float top=SF(titleH+headH);g.DrawLine(line,pad,top-SF(4),Width-pad,top-SF(4));
                int visible=VisibleRows();
                if(Rows.Count==0)g.DrawString("暂无进程记录",Theme.GetFont(10,FontStyle.Regular,false),muted,new RectangleF(pad,top+SF(20),wide,SF(30)),clip);
                for(int i=0;i<visible&&Offset+i<Rows.Count;i++)
                {
                    ProcRow row=Rows[Offset+i];float y=top+i*SF(RowHeight);
                    if(row.Kept || (Offset+i)%2==1)using(SolidBrush b=new SolidBrush(row.Kept?Color.FromArgb(229,240,252):AlternateRow))g.FillRectangle(b,pad-SF(6),y,wide+SF(12),SF(RowHeight));
                    if(Selectable&&row.Keepable)DrawCheck(g,pad,y+SF(10),row.Kept);
                    float stateX=pad+(Selectable?SF(24):0);
                    using(SolidBrush b=new SolidBrush(row.StateColor))g.DrawString(row.State,Theme.GetFont(9F,FontStyle.Regular,false),b,new RectangleF(stateX,y+SF(9),nameX-stateX-SF(4),SF(22)),clip);
                    float textX = nameX;
                    if (row.IconPath != null)
                    {
                        float iconSize = SF(16);
                        g.DrawImage(RowIcon(row.IconPath), nameX, y + (SF(RowHeight) - iconSize) / 2, iconSize, iconSize);
                        textX += SF(24);
                    }
                    float nameRight = showMemory ? memX - SF(76) : contentRight;
                    float nameWidth = Math.Max(SF(24), nameRight - textX - SF(8));
                    bool showPathUnderName = !ShowPath && (row.IsProblem || row.CanKeepNextTime);
                    g.DrawString(row.Name,Theme.GetFont(9.5F,FontStyle.Regular,false),ink,
                        new RectangleF(textX,y+SF(showPathUnderName?3:9),nameWidth,SF(22)),clip);
                    if (showPathUnderName)
                        g.DrawString(row.IsProblem ? row.Path : row.IconPath,Theme.GetFont(8F,FontStyle.Regular,false),muted,
                            new RectangleF(textX,y+SF(19),nameWidth,SF(16)),clip);
                    if(showMemory)g.DrawString(row.Memory,Theme.GetFont(9.5F,FontStyle.Regular,true),ink,new RectangleF(memX-SF(76),y+SF(9),SF(76),SF(22)),right);
                    if(ShowPath)g.DrawString(row.Path,Theme.GetFont(8.5F,FontStyle.Regular,false),muted,new RectangleF(pathX,y+SF(10),contentRight-pathX,SF(22)),clip);
                    if(i<visible-1&&Offset+i<Rows.Count-1)g.DrawLine(line,pad,y+SF(RowHeight),Width-pad,y+SF(RowHeight));
                }
                if(Rows.Count>visible)
                {
                    float h=Height-top-SF(12),thumb=Math.Max(SF(28),h*visible/Rows.Count),y=top+(h-thumb)*Offset/Math.Max(1F,Rows.Count-visible);
                    using(GraphicsPath p=Theme.Rounded(new RectangleF(Width-SF(9),y,SF(4),thumb),SF(2)))using(SolidBrush b=new SolidBrush(Theme.SecondaryLine))g.FillPath(b,p);
                }
            }
        }
    }
}
