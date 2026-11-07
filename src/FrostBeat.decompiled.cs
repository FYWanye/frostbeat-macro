using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using Mono.Cecil;
using Mono.Cecil.Cil;

[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: AssemblyProduct("FrostBeat")]
[assembly: CompilationRelaxations(8)]
[assembly: AssemblyTitle("冰与火之宏")]
[assembly: AssemblyVersion("1.0.0.0")]
namespace FrostBeat;

public static class Program
{
	// 崩溃修复：集中日志目录，供异常处理与诊断写入。
	private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrostBeat");

	private static void Log(string text)
	{
		try
		{
			Directory.CreateDirectory(LogDir);
			File.AppendAllText(Path.Combine(LogDir, "frostbeat.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine);
		}
		catch
		{
		}
	}

	// 崩溃修复：全局异常兜底，记录日志而非静默闪退（后台线程异常也能被捕获）。
	private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
	{
		Log("UnhandledException: " + (e.ExceptionObject as Exception));
	}

	private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
	{
		Log("ThreadException: " + e.Exception);
	}

	[STAThread]
	public static void Main(string[] args)
	{
		// 崩溃修复：尽早挂接全局异常处理，避免后台线程/未处理异常导致静默闪退。
		AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
		System.Windows.Forms.Application.ThreadException += OnThreadException;

		if (args.Length >= 2 && args[0] == "--install")
		{
			try
			{
				Installer.Install(args[1]);
				Console.WriteLine("install ok");
			}
			catch (Exception ex)
			{
				Log("install failed: " + ex);
				Console.Error.WriteLine(ex.Message);
				Environment.ExitCode = 1;
			}
			return;
		}
		if (args.Length >= 2 && args[0] == "--uninstall")
		{
			try
			{
				Installer.Uninstall(args[1]);
				Console.WriteLine("uninstall ok");
			}
			catch (Exception ex)
			{
				Log("uninstall failed: " + ex);
				Console.Error.WriteLine(ex.Message);
				Environment.ExitCode = 1;
			}
			return;
		}
		if (args.Length >= 2 && args[0] == "--snapshot")
		{
			try
			{
				using (Reader reader = new Reader())
				{
					Snapshot snapshot = reader.Read(int.Parse(args[1]));
					if (snapshot != null)
					{
						Console.WriteLine(snapshot.Title + " | flags=" + snapshot.Flags + " song=" + snapshot.Song + " pitch=" + snapshot.Pitch + " seq=" + snapshot.Current + " events=" + snapshot.Beats.Length + " reason=" + snapshot.Reason);
					}
					else
					{
						Console.WriteLine("no bridge");
					}
				}
			}
			catch (Exception ex)
			{
				Log("snapshot failed: " + ex);
				Console.Error.WriteLine(ex.Message);
				Environment.ExitCode = 1;
			}
			return;
		}
		bool createdNew;
		using (new Mutex(initiallyOwned: true, "Local\\FrostBeat.Desktop", out createdNew))
		{
			if (!createdNew)
			{
				System.Windows.MessageBox.Show("冰与火之宏已经运行。");
				return;
			}
			System.Windows.Application application = new System.Windows.Application();
			// 崩溃修复：异常弹窗带节流（同一消息 2 秒内不重复弹出），并写入日志，
			// 避免 Update() 周期异常导致弹窗风暴卡死界面。
			DateTime lastDialog = DateTime.MinValue;
			string lastMessage = "";
			application.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
			{
				Log("DispatcherUnhandledException: " + e.Exception);
				string msg = e.Exception.Message ?? "";
				DateTime now2 = DateTime.Now;
				if (msg != lastMessage || (now2 - lastDialog).TotalSeconds > 2.0)
				{
					lastMessage = msg;
					lastDialog = now2;
					System.Windows.MessageBox.Show(msg, "冰与火之宏");
				}
				e.Handled = true;
			};
			try
			{
				application.Run(new MainUI().Window);
			}
			catch (Exception ex)
			{
				Log("Fatal: " + ex);
				throw;
			}
		}
	}
}
public sealed class MainUI
{
	public Window Window;

	private readonly Engine engine = new Engine();

	private readonly DispatcherTimer timer = new DispatcherTimer();

	private readonly Overlay overlay;

	private IntPtr handle;

	private string root = "";

	private int ticks;

	private long seenGesture;

	private long leftTick;

	private long rightTick;

	private string leftKey = "—";

	private string rightKey = "—";

	private string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrostBeat", "settings.xml");

	private T Find<T>(string n) where T : class
	{
		return Window.FindName(n) as T;
	}

	private void Text(string n, string s)
	{
		Find<TextBlock>(n).Text = s;
	}

	public MainUI()
	{
		using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
		{
			Window = (Window)XamlReader.Load(stream);
		}
		foreach (Inline inline in Find<TextBlock>("CreditsText").Inlines)
		{
			if (!(inline is Hyperlink hyperlink))
			{
				continue;
			}
			hyperlink.RequestNavigate += delegate(object s, RequestNavigateEventArgs e)
			{
				try
				{
					Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
					{
						UseShellExecute = true
					});
				}
				catch (Exception ex)
				{
					System.Windows.MessageBox.Show(ex.Message, "无法打开链接", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				}
				e.Handled = true;
			};
		}
		overlay = new Overlay(Window);
		root = Game.Locate();
		Find<System.Windows.Controls.TextBox>("GamePath").Text = root;
		Load();
		overlay.Apply();
		if (double.IsNaN(Window.Left))
		{
			Window.Left = SystemParameters.WorkArea.Right - Window.Width - 20.0;
		}
		if (double.IsNaN(Window.Top))
		{
			Window.Top = SystemParameters.WorkArea.Top + 40.0;
		}
		ModeChanged();
		Find<System.Windows.Controls.Button>("ExpandButton").Click += delegate
		{
			Toggle();
		};
		Find<System.Windows.Controls.CheckBox>("HumanizeBox").Checked += delegate
		{
			ModeChanged();
		};
		Find<System.Windows.Controls.CheckBox>("HumanizeBox").Unchecked += delegate
		{
			ModeChanged();
		};
		Find<System.Windows.Controls.CheckBox>("ScaleDelayBox").Checked += delegate
		{
			DelayModeChanged();
		};
		Find<System.Windows.Controls.CheckBox>("ScaleDelayBox").Unchecked += delegate
		{
			DelayModeChanged();
		};
		DelayModeChanged();
		Find<System.Windows.Controls.TextBox>("KeysBox").TextChanged += delegate
		{
			KeyPlan();
		};
		Find<System.Windows.Controls.TextBox>("KeysBox").PreviewKeyDown += CaptureKey;
		Find<System.Windows.Controls.TextBox>("KeysBox").GotKeyboardFocus += delegate
		{
			Text("KeyPlanText", "点击按键框后，直接按下要添加的实体按键；按 Ctrl+A 清空列表。英文逗号键会显示为 Comma。 ");
		};
		KeyPlan();
		Find<System.Windows.Controls.Button>("HandPresetButton").Click += delegate
		{
			HandPreset();
		};
		Find<Slider>("ScaleSlider").ValueChanged += delegate(object s, RoutedPropertyChangedEventArgs<double> e)
		{
			overlay.SetScale(e.NewValue / 100.0);
			Text("ScaleText", "大小 " + e.NewValue.ToString("F0") + "%");
		};
		Find<Slider>("OpacitySlider").ValueChanged += delegate(object s, RoutedPropertyChangedEventArgs<double> e)
		{
			Window.Opacity = e.NewValue / 100.0;
			Text("OpacityText", "透明度 " + e.NewValue.ToString("F0") + "%");
		};
		Find<System.Windows.Controls.Button>("ArmButton").Click += delegate
		{
			if (engine.Armed)
			{
				engine.Stop("已停止 · 待命按钮");
			}
			else
			{
				Arm();
			}
		};
		Find<System.Windows.Controls.Button>("StopButton").Click += delegate
		{
			engine.Stop("已停止 · F9 / 停止按钮");
		};
		Find<System.Windows.Controls.Button>("InstallButton").Click += delegate
		{
			Action(delegate
			{
				root = Find<System.Windows.Controls.TextBox>("GamePath").Text.Trim();
				Installer.Install(root);
				System.Windows.MessageBox.Show("读取模块已安装，原文件已校验备份。现在可启动游戏。", "安装完成");
			});
		};
		Find<System.Windows.Controls.Button>("UninstallButton").Click += delegate
		{
			Action(delegate
			{
				engine.Stop("正在卸载");
				root = Find<System.Windows.Controls.TextBox>("GamePath").Text.Trim();
				Installer.Uninstall(root);
				System.Windows.MessageBox.Show("已恢复原始游戏文件。原文件备份仍保留。", "卸载完成");
			});
		};
		Find<System.Windows.Controls.Button>("BrowseButton").Click += delegate
		{
			using System.Windows.Forms.OpenFileDialog openFileDialog = new System.Windows.Forms.OpenFileDialog();
			openFileDialog.Filter = "冰与火之舞|A Dance of Fire and Ice.exe";
			openFileDialog.Title = "选择游戏程序";
			if (openFileDialog.ShowDialog() == DialogResult.OK)
			{
				root = Path.GetDirectoryName(openFileDialog.FileName);
				Find<System.Windows.Controls.TextBox>("GamePath").Text = root;
			}
		};
		Find<System.Windows.Controls.Button>("LaunchButton").Click += delegate
		{
			Action(delegate
			{
				if (Game.Find() == null)
				{
					Process.Start(new ProcessStartInfo("steam://rungameid/977950")
					{
						UseShellExecute = true
					});
				}
			});
		};
		Find<System.Windows.Controls.CheckBox>("TopmostBox").Checked += delegate
		{
			Window.Topmost = true;
		};
		Find<System.Windows.Controls.CheckBox>("TopmostBox").Unchecked += delegate
		{
			Window.Topmost = false;
		};
		Window.SourceInitialized += delegate
		{
			handle = new WindowInteropHelper(Window).Handle;
			HwndSource.FromHwnd(handle).AddHook(Hook);
			Native.OverlayFocus(handle, !overlay.Expanded);
			bool flag = Native.RegisterHotKey(handle, 1, 16384u, 119u);
			bool flag2 = Native.RegisterHotKey(handle, 2, 16384u, 120u);
			bool flag3 = Native.RegisterHotKey(handle, 3, 16386u, 121u);
			if (!flag || !flag2 || !flag3)
			{
				Text("NoteText", "部分热键被其他软件占用，请使用悬浮窗上的按钮。");
			}
		};
		Window.Closed += delegate
		{
			timer.Stop();
			engine.Dispose();
			Native.UnregisterHotKey(handle, 1);
			Native.UnregisterHotKey(handle, 2);
			Native.UnregisterHotKey(handle, 3);
			Save();
		};
		timer.Interval = TimeSpan.FromMilliseconds(100.0);
		timer.Tick += delegate
		{
			Update();
		};
		timer.Start();
		Update();
	}

	private IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
	{
		if (msg == 33 && !overlay.Expanded)
		{
			handled = true;
			return new IntPtr(3);
		}
		if (msg == 786)
		{
			handled = true;
			if (w.ToInt32() == 3)
			{
				Toggle();
			}
			else if (w.ToInt32() == 2)
			{
				engine.Stop("已停止 · F9");
			}
			else if (w.ToInt32() == 1)
			{
				if (engine.Armed)
				{
					engine.Stop("已停止 · F8");
				}
				else
				{
					Arm();
				}
			}
		}
		return IntPtr.Zero;
	}

	private void Toggle()
	{
		if (!overlay.Expanded)
		{
			engine.Stop("已停止 · 调整设置后按 F8 待命");
		}
		Window.WindowState = WindowState.Normal;
		overlay.SetExpanded(!overlay.Expanded);
		Native.OverlayFocus(handle, !overlay.Expanded);
		if (overlay.Expanded)
		{
			Window.Activate();
		}
		Save();
	}

	private void ModeChanged()
	{
		bool flag = Find<System.Windows.Controls.CheckBox>("HumanizeBox").IsChecked == true;
		Find<FrameworkElement>("RollPanel").Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		Find<FrameworkElement>("HandProfilePanel").Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		Find<TextBlock>("KeyPlanText").Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		Text("HandHint", flag ? "从中间常用键开始，先加左键，再左右交替；每档按预览顺序循环。" : "按填写顺序轮换按键，忙碌的按键会跳过。");
		Text("ModeHint", "两种模式共用全部延迟设置；按住时长不随速度改变。");
	}

	private void CaptureKey(object sender, System.Windows.Input.KeyEventArgs e)
	{
		System.Windows.Controls.TextBox textBox = Find<System.Windows.Controls.TextBox>("KeysBox");
		Key key = ((e.Key == Key.System) ? e.SystemKey : e.Key);
		if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && key == Key.A)
		{
			textBox.Clear();
			textBox.CaretIndex = 0;
			e.Handled = true;
			return;
		}
		e.Handled = true;
		int num = KeyInterop.VirtualKeyFromKey(key);
		if (num <= 0 || num > 65535)
		{
			return;
		}
		ushort num2 = Native.NormalizeKey((ushort)num);
		if (Native.IsUnsupportedKey(num2))
		{
			System.Windows.MessageBox.Show("不支持输入以下按键：Esc、Win、F1~F12、PrtSc、Delete、CapsLK、NumLock、Copilot", "不支持的按键", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		string text = Native.KeyLabel(num2);
		if (text.StartsWith("VK", StringComparison.Ordinal))
		{
			return;
		}
		string text2 = textBox.Text ?? "";
		try
		{
			ushort[] array = ((text2.Trim().Length == 0) ? new ushort[0] : Native.ParseKeys(text2));
			if (Array.IndexOf(array, num2) >= 0)
			{
				return;
			}
			if (array.Length >= 32)
			{
				System.Windows.MessageBox.Show("最多只能设置 32 个不重复的按键。", "按键数量已达上限", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
		}
		catch (ArgumentException)
		{
			text2 = "";
		}
		string text3 = ((textBox.SelectionLength > 0 && textBox.SelectionLength == textBox.Text.Length) ? text : (string.IsNullOrWhiteSpace(text2) ? text : (text2.TrimEnd(',') + "," + text)));
		textBox.Text = text3;
		textBox.CaretIndex = textBox.Text.Length;
		textBox.SelectionLength = 0;
		Save();
	}

	private void HandPreset()
	{
		engine.Stop("已停止 · ASDFHJKL 手法已设置");
		Find<System.Windows.Controls.CheckBox>("HumanizeBox").IsChecked = true;
		Find<System.Windows.Controls.TextBox>("KeysBox").Text = "A,S,D,F,H,J,K,L";
		KeyPlan();
		Save();
	}

	private void DelayModeChanged()
	{
		Find<FrameworkElement>("DelaySpeedPanel").IsEnabled = !engine.Armed && Find<System.Windows.Controls.CheckBox>("ScaleDelayBox").IsChecked == true;
	}

	private void KeyPlan()
	{
		try
		{
			Options options = new Options();
			options.Keys = Native.ParseKeys(Find<System.Windows.Controls.TextBox>("KeysBox").Text);
			Options options2 = options;
			HandProfile handProfile = HandProfile.Build(options2);
			Text("CommonKeyText", "常用：" + Humanizer.KeyName(handProfile.Main) + ((handProfile.Aux != 0) ? (" · 辅助：" + Humanizer.KeyName(handProfile.Aux)) : ""));
			Find<System.Windows.Controls.TextBox>("StagePlanBox").Text = handProfile.Preview(options2.Keys);
			Text("KeyPlanText", "加键：" + HandProfile.Names(options2.Keys, handProfile.Priority) + "\n升降档从该档首键开始，同档从左到右循环。");
		}
		catch (ArgumentException ex)
		{
			Text("CommonKeyText", "按键设置待完善");
			Find<System.Windows.Controls.TextBox>("StagePlanBox").Text = "";
			Text("KeyPlanText", ex.Message);
		}
	}

	private void Action(Action a)
	{
		try
		{
			a();
			ticks = 0;
			Update();
			Save();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show(ex.Message, "冰与火之宏", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
	}

	private static double Number(string text, double min, double max, string label)
	{
		if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || double.IsNaN(result) || double.IsInfinity(result) || result < min || result > max)
		{
			throw new Exception(label + "需在 " + min + " 到 " + max + " 之间。");
		}
		return result;
	}

	private void Arm()
	{
		Action(delegate
		{
			if (engine.Pid <= 0)
			{
				throw new Exception("未检测到游戏进程，请先启动冰与火之舞。");
			}
			if (engine.State == null)
			{
				throw new Exception("读取模块尚未连接。首次使用请退出游戏后安装模块，再重新启动游戏。");
			}
			bool flag = Find<System.Windows.Controls.CheckBox>("HumanizeBox").IsChecked == true;
			bool flag2 = Find<System.Windows.Controls.CheckBox>("ScaleDelayBox").IsChecked == true;
			Options o = new Options
			{
				Offset = Number(Find<System.Windows.Controls.TextBox>("OffsetBox").Text, -250.0, 250.0, "固定偏移"),
				Error = Number(Find<System.Windows.Controls.TextBox>("ErrorBox").Text, 0.0, 100.0, "随机延迟上限"),
				Press = Number(Find<System.Windows.Controls.TextBox>("KeyDurationBox").Text, 1.0, 1000.0, "按下到松开时长"),
				ScaleDelay = flag2,
				DelayFullSpeed = (flag2 ? Number(Find<System.Windows.Controls.TextBox>("DelayFullSpeedBox").Text, 0.5, 100.0, "达到上限速度") : 10.0),
				RollSpeed = (flag ? Number(Find<System.Windows.Controls.TextBox>("RollSpeedBox").Text, 2.0, 10.0, "加键速度") : 4.0),
				Humanize = flag,
				Keys = Native.ParseKeys(Find<System.Windows.Controls.TextBox>("KeysBox").Text)
			};
			if (flag)
			{
				HandProfile.Build(o);
			}
			engine.Arm(o);
		});
	}

	private void Update()
	{
		if (ticks++ % 10 == 0)
		{
			Process process = Game.Find();
			engine.Pid = process?.Id ?? 0;
			Text("ProcessText", (process == null) ? "● 游戏未运行" : ("● 游戏已运行  /  PID " + process.Id));
			root = Find<System.Windows.Controls.TextBox>("GamePath").Text.Trim();
			Text("ModuleText", Installer.Installed(root) ? "读取模块：已安装" : "读取模块：未安装 / 待重装");
		}
		Snapshot state = engine.State;
		Text("ChartText", (state == null) ? "等待读取谱面" : (string.IsNullOrWhiteSpace(state.Title) ? "当前谱面" : state.Title));
		Find<TextBlock>("ChartText").ToolTip = Find<TextBlock>("ChartText").Text;
		Text("StatusText", engine.Status + ((state != null && state.Reason.Length > 0) ? (" · " + state.Reason) : ""));
		Text("StatsText", "砖块 " + ((state == null) ? "— / —" : (state.Current + " / " + Math.Max(0, state.Total - 1))) + "  ·  击键 " + engine.Sent);
		Find<TextBlock>("StatsText").ToolTip = "最近调度迟到 " + engine.LastError.ToString("F2") + " ms；不等于游戏命中误差。";
		Gesture lastGesture = engine.LastGesture;
		long now = Clock.Now;
		if (lastGesture != null && lastGesture.Tick != seenGesture)
		{
			seenGesture = lastGesture.Tick;
			string text = Native.KeyLabel(lastGesture.Key);
			if (lastGesture.Hand == 0)
			{
				leftKey = text;
				leftTick = lastGesture.Tick;
			}
			else if (lastGesture.Hand == 1)
			{
				rightKey = text;
				rightTick = lastGesture.Tick;
			}
			else
			{
				leftKey = text;
				leftTick = lastGesture.Tick;
			}
		}
		Text("LeftHandText", "左手  " + leftKey);
		Text("RightHandText", "右手  " + rightKey);
		Find<TextBlock>("LeftHandText").Opacity = (((double)(now - leftTick) / (double)Clock.Frequency < 0.22) ? 1.0 : 0.55);
		Find<TextBlock>("RightHandText").Opacity = (((double)(now - rightTick) / (double)Clock.Frequency < 0.22) ? 1.0 : 0.55);
		bool flag = Find<System.Windows.Controls.CheckBox>("HumanizeBox").IsChecked == true;
		Text("GestureText", (lastGesture == null || !engine.Armed) ? (flag ? "逐档轮指" : "顺序轮换") : (double.IsPositiveInfinity(lastGesture.Press) ? "Hold 持续按住" : ((!flag) ? (lastGesture.Press.ToString("F0") + " ms 轻点") : (lastGesture.Borrowed ? ("借键 " + Humanizer.KeyName(lastGesture.Key)) : ((lastGesture.KeyCount == 1) ? "单键轻点" : (lastGesture.KeyCount + " 键轮指"))))));
		Find<TextBlock>("GestureText").ToolTip = ((lastGesture == null) ? "按可用按键列表自动生成各档手势，展开可查看" : ("使用：" + lastGesture.ActiveKeys + " · " + (double.IsPositiveInfinity(lastGesture.Press) ? "Hold" : (lastGesture.Press.ToString("F0") + " ms 轻点")) + (lastGesture.Borrowed ? "；当前键位被占用，临时借用空闲键" : "")));
		string text2 = ((lastGesture == null) ? "最近模拟：等待击键" : ("范围 ±" + lastGesture.DelayLimit.ToString("F1") + " ms · 谱面 " + lastGesture.DelayRate.ToString("F1") + " 次/秒\n本次偏移 " + lastGesture.Jitter.ToString("+0.0;-0.0;0.0") + " ms · " + (double.IsPositiveInfinity(lastGesture.Press) ? "Hold 按谱面松开" : ("按住 " + lastGesture.Press.ToString("F0") + " ms"))));
		Text("InputTimingText", text2);
		TextBlock textBlock = Find<TextBlock>("GestureText");
		textBlock.ToolTip = string.Concat(textBlock.ToolTip, "\n", text2);
		Find<System.Windows.Controls.ProgressBar>("Progress").Value = ((state == null || state.Total <= 1) ? 0.0 : Math.Min(100.0, 100.0 * (double)state.Current / (double)(state.Total - 1)));
		Find<System.Windows.Controls.Button>("ArmButton").Content = (engine.Armed ? "已待命 · F8" : "待命 F8");
		string[] array = new string[5] { "OffsetBox", "ErrorBox", "KeyDurationBox", "RollSpeedBox", "KeysBox" };
		string[] array2 = array;
		foreach (string n in array2)
		{
			Find<System.Windows.Controls.TextBox>(n).IsEnabled = !engine.Armed;
		}
		Find<System.Windows.Controls.CheckBox>("HumanizeBox").IsEnabled = !engine.Armed;
		Find<System.Windows.Controls.CheckBox>("ScaleDelayBox").IsEnabled = !engine.Armed;
		Find<System.Windows.Controls.Button>("HandPresetButton").IsEnabled = !engine.Armed;
		DelayModeChanged();
	}

	private static double Preference(XElement e, string n, double fallback, double min, double max)
	{
		if (e.Element(n) == null || !double.TryParse((string)e.Element(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || double.IsNaN(result) || double.IsInfinity(result))
		{
			return fallback;
		}
		return Math.Max(min, Math.Min(max, result));
	}

	private void Load()
	{
		Window.Opacity = 0.92;
		try
		{
			XElement xElement = XDocument.Load(settings).Root;
			string[] array = new string[6] { "OffsetBox", "ErrorBox", "KeyDurationBox", "RollSpeedBox", "KeysBox", "DelayFullSpeedBox" };
			foreach (string text in array)
			{
				if (xElement.Element(text) != null)
				{
					Find<System.Windows.Controls.TextBox>(text).Text = (string)xElement.Element(text);
				}
			}
			Find<System.Windows.Controls.TextBox>("KeyDurationBox").Text = InputPreferences.Duration(xElement);
			string[] array2 = new string[3] { "HumanizeBox", "TopmostBox", "ScaleDelayBox" };
			foreach (string text2 in array2)
			{
				if (xElement.Element(text2) != null && bool.TryParse((string)xElement.Element(text2), out var result))
				{
					Find<System.Windows.Controls.CheckBox>(text2).IsChecked = result;
				}
			}
			overlay.Expanded = bool.TryParse((string)xElement.Element("Expanded"), out var result2) && result2;
			overlay.Scale = Preference(xElement, "Scale", 1.0, 0.8, 1.4);
			overlay.CompactWidth = Preference(xElement, "CompactWidth", 350.0, 300.0, 1400.0);
			overlay.CompactHeight = Preference(xElement, "CompactHeight", 248.0, 230.0, 1200.0);
			overlay.ExpandedWidth = Preference(xElement, "ExpandedWidth", 440.0, 380.0, 1400.0);
			overlay.ExpandedHeight = Preference(xElement, "ExpandedHeight", 740.0, 500.0, 1200.0);
			if (xElement.Element("Left") != null)
			{
				Window.Left = Preference(xElement, "Left", 0.0, -32000.0, 32000.0);
			}
			if (xElement.Element("Top") != null)
			{
				Window.Top = Preference(xElement, "Top", 0.0, -32000.0, 32000.0);
			}
			Window.Opacity = Preference(xElement, "Opacity", 0.92, 0.45, 1.0);
		}
		catch
		{
		}
		Window.Topmost = Find<System.Windows.Controls.CheckBox>("TopmostBox").IsChecked == true;
		Find<Slider>("ScaleSlider").Value = overlay.Scale * 100.0;
		Find<Slider>("OpacitySlider").Value = Window.Opacity * 100.0;
		Text("ScaleText", "大小 " + (overlay.Scale * 100.0).ToString("F0") + "%");
		Text("OpacityText", "透明度 " + (Window.Opacity * 100.0).ToString("F0") + "%");
	}

	private void Save()
	{
		try
		{
			overlay.Remember();
			Directory.CreateDirectory(Path.GetDirectoryName(settings));
			XElement xElement = new XElement("settings");
			string[] array = new string[6] { "OffsetBox", "ErrorBox", "KeyDurationBox", "RollSpeedBox", "KeysBox", "DelayFullSpeedBox" };
			foreach (string text in array)
			{
				xElement.Add(new XElement(text, Find<System.Windows.Controls.TextBox>(text).Text));
			}
			string[] array2 = new string[3] { "HumanizeBox", "TopmostBox", "ScaleDelayBox" };
			foreach (string text2 in array2)
			{
				xElement.Add(new XElement(text2, Find<System.Windows.Controls.CheckBox>(text2).IsChecked == true));
			}
			xElement.Add(new XElement("Expanded", overlay.Expanded), new XElement("Scale", overlay.Scale), new XElement("Opacity", Window.Opacity), new XElement("CompactWidth", overlay.CompactWidth), new XElement("CompactHeight", overlay.CompactHeight), new XElement("ExpandedWidth", overlay.ExpandedWidth), new XElement("ExpandedHeight", overlay.ExpandedHeight), new XElement("Left", Window.Left), new XElement("Top", Window.Top));
			new XDocument(xElement).Save(settings);
		}
		catch
		{
		}
	}
}
public static class Game
{
	public const string Exe = "A Dance of Fire and Ice.exe";

	public static Process Find()
	{
		return Process.GetProcessesByName("A Dance of Fire and Ice").FirstOrDefault(delegate(Process p)
		{
			try
			{
				return !p.HasExited;
			}
			catch
			{
				return false;
			}
		});
	}

	public static string Managed(string root)
	{
		return Path.Combine(root, "A Dance of Fire and Ice_Data", "Managed");
	}

	public static bool Valid(string root)
	{
		if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "A Dance of Fire and Ice.exe")))
		{
			return File.Exists(Path.Combine(Managed(root), "Assembly-CSharp.dll"));
		}
		return false;
	}

	public static string Locate()
	{
		Process process = Find();
		if (process != null)
		{
			try
			{
				return Path.GetDirectoryName(process.MainModule.FileName);
			}
			catch
			{
			}
		}
		List<string> list = new List<string>();
		// 崩溃修复：注册表访问加防御，Steam 未安装/键缺失时不抛异常。
		try
		{
			using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam"))
			{
				if (registryKey != null)
				{
					string sp = Convert.ToString(registryKey.GetValue("SteamPath"));
					if (!string.IsNullOrEmpty(sp))
					{
						list.Add(sp);
					}
				}
			}
		}
		catch
		{
		}
		list.Add("C:\\Program Files (x86)\\Steam");
		list.Add("D:\\steam");
		string[] array = list.ToArray();
		foreach (string path in array)
		{
			string path2 = Path.Combine(path, "steamapps", "libraryfolders.vdf");
			if (!File.Exists(path2))
			{
				continue;
			}
			// 崩溃修复：vdf 文件读取加防御，文件被锁/损坏/编码异常时不崩溃。
			try
			{
				foreach (Match item in Regex.Matches(File.ReadAllText(path2), "\"path\"\\s+\"([^\"]+)\""))
				{
					list.Add(item.Groups[1].Value.Replace("\\\\", "\\"));
				}
			}
			catch
			{
			}
		}
		foreach (string item2 in list.Distinct())
		{
			string path3 = Path.Combine(item2, "steamapps", "appmanifest_977950.acf");
			if (!File.Exists(path3))
			{
				continue;
			}
			// 崩溃修复：acf 文件读取加防御。
			try
			{
				Match match2 = Regex.Match(File.ReadAllText(path3), "\"installdir\"\\s+\"([^\"]+)\"");
				if (match2.Success)
				{
					string text = Path.Combine(item2, "steamapps", "common", match2.Groups[1].Value);
					if (Valid(text))
					{
						return text;
					}
				}
			}
			catch
			{
			}
		}
		return "";
	}
}
public static class Installer
{
	public static string Hash(string file)
	{
		using SHA256 sHA = SHA256.Create();
		using FileStream inputStream = File.OpenRead(file);
		return BitConverter.ToString(sHA.ComputeHash(inputStream)).Replace("-", "").ToLowerInvariant();
	}

	private static string Record(string root)
	{
		return Path.Combine(root, "FrostBeat.install.xml");
	}

	public static bool Installed(string root)
	{
		try
		{
			XDocument xDocument = XDocument.Load(Record(root));
			return Hash(Path.Combine(Game.Managed(root), "Assembly-CSharp.dll")) == (string)xDocument.Root.Element("patched") && Hash(Path.Combine(Game.Managed(root), "FrostBeat.Bridge.dll")) == (string)xDocument.Root.Element("bridge");
		}
		catch
		{
			return false;
		}
	}

	public static void Install(string root)
	{
		if (!Game.Valid(root))
		{
			throw new Exception("请选择游戏根目录，需包含 A Dance of Fire and Ice.exe。");
		}
		if (Game.Find() != null)
		{
			throw new Exception("请先正常退出游戏，再安装读取模块。");
		}
		if (Installed(root))
		{
			string file = Path.Combine(Game.Managed(root), "FrostBeat.Bridge.dll");
			string file2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FrostBeat.Bridge.dll");
			if (Hash(file) == Hash(file2))
			{
				return;
			}
			Uninstall(root);
		}
		string text = Path.Combine(Game.Managed(root), "Assembly-CSharp.dll");
		string text2 = Hash(text);
		string text3 = Path.Combine(root, "FrostBeatBackup");
		Directory.CreateDirectory(text3);
		string text4 = Path.Combine(text3, text2 + ".dll");
		if (!File.Exists(text4))
		{
			File.Copy(text, text4);
		}
		if (Hash(text4) != text2)
		{
			throw new Exception("原始文件备份校验失败。");
		}
		string text5 = text + ".frostbeat-stage";
		string text6 = Path.Combine(Game.Managed(root), "FrostBeat.Bridge.dll");
		string text7 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FrostBeat.Bridge.dll");
		bool flag = false;
		DefaultAssemblyResolver defaultAssemblyResolver = new DefaultAssemblyResolver();
		defaultAssemblyResolver.AddSearchDirectory(Game.Managed(root));
		try
		{
			using (ModuleDefinition moduleDefinition = ModuleDefinition.ReadModule(text, new ReaderParameters
			{
				AssemblyResolver = defaultAssemblyResolver,
				InMemory = true
			}))
			{
				if (moduleDefinition.AssemblyReferences.Any((AssemblyNameReference a) => a.Name == "FrostBeat.Bridge"))
				{
					throw new Exception("发现已有读取补丁但安装记录不完整，请先恢复游戏文件或原始备份。");
				}
				MethodDefinition methodDefinition = moduleDefinition.Types.FirstOrDefault((TypeDefinition t) => t.Name == "scrConductor")?.Methods.FirstOrDefault((MethodDefinition m) => m.Name == "Update" && m.Parameters.Count == 0);
				if (methodDefinition == null || !methodDefinition.HasBody)
				{
					throw new Exception("此游戏版本暂不兼容读取模块。");
				}
				AssemblyNameReference assemblyNameReference = new AssemblyNameReference("FrostBeat.Bridge", new Version(1, 0, 0, 0));
				moduleDefinition.AssemblyReferences.Add(assemblyNameReference);
				TypeReference declaringType = new TypeReference("FrostBeat", "Bridge", moduleDefinition, assemblyNameReference);
				MethodReference methodReference = new MethodReference("Tick", moduleDefinition.TypeSystem.Void, declaringType);
				methodReference.HasThis = false;
				MethodReference methodReference2 = methodReference;
				methodReference2.Parameters.Add(new ParameterDefinition(moduleDefinition.TypeSystem.Object));
				Instruction[] array = methodDefinition.Body.Instructions.Where((Instruction i) => i.OpCode == OpCodes.Ret).ToArray();
				foreach (Instruction instruction in array)
				{
					instruction.OpCode = OpCodes.Ldarg_0;
					instruction.Operand = null;
					Instruction instruction2 = Instruction.Create(OpCodes.Call, methodReference2);
					methodDefinition.Body.GetILProcessor().InsertAfter(instruction, instruction2);
					methodDefinition.Body.GetILProcessor().InsertAfter(instruction2, Instruction.Create(OpCodes.Ret));
				}
				methodDefinition.Body.MaxStackSize = Math.Max(methodDefinition.Body.MaxStackSize, 1);
				MethodDefinition methodDefinition2 = moduleDefinition.Types.FirstOrDefault((TypeDefinition t) => t.Name == "scrLevelMaker")?.Methods.FirstOrDefault((MethodDefinition m) => m.Name == "CalculateFloorEntryTimes");
				if (methodDefinition2 != null && methodDefinition2.HasBody)
				{
					MethodReference methodReference3 = new MethodReference("MarkDirty", moduleDefinition.TypeSystem.Void, declaringType);
					methodReference3.HasThis = false;
					MethodReference operand = methodReference3;
					Instruction[] array2 = methodDefinition2.Body.Instructions.Where((Instruction i) => i.OpCode == OpCodes.Ret).ToArray();
					foreach (Instruction instruction3 in array2)
					{
						instruction3.OpCode = OpCodes.Call;
						instruction3.Operand = operand;
						methodDefinition2.Body.GetILProcessor().InsertAfter(instruction3, Instruction.Create(OpCodes.Ret));
					}
				}
				moduleDefinition.Write(text5);
			}
			string content = Hash(text5);
			if (File.Exists(text6))
			{
				if (Hash(text6) != Hash(text7))
				{
					throw new Exception("已有读取模块与此版本不一致，请先卸载旧模块或恢复其安装记录。");
				}
			}
			else
			{
				File.Copy(text7, text6);
				flag = true;
			}
			File.Replace(text5, text, null);
			try
			{
				new XDocument(new XElement("installation", new XElement("original", text2), new XElement("patched", content), new XElement("backup", Path.GetFileName(text4)), new XElement("bridge", Hash(text6)))).Save(Record(root));
			}
			catch
			{
				File.Copy(text4, text5, overwrite: true);
				File.Replace(text5, text, null);
				if (flag)
				{
					File.Delete(text6);
				}
				flag = false;
				throw;
			}
		}
		catch
		{
			if (flag && !Installed(root) && Hash(text) == text2)
			{
				File.Delete(text6);
			}
			throw;
		}
		finally
		{
			if (File.Exists(text5))
			{
				File.Delete(text5);
			}
		}
	}

	public static void Uninstall(string root)
	{
		if (Game.Find() != null)
		{
			throw new Exception("请先正常退出游戏，再卸载读取模块。");
		}
		XDocument xDocument = XDocument.Load(Record(root));
		string text = Path.Combine(Game.Managed(root), "Assembly-CSharp.dll");
		if (Hash(text) != (string)xDocument.Root.Element("patched"))
		{
			throw new Exception("游戏文件已经变化，已保留备份；请使用 Steam 验证文件，避免覆盖游戏更新。");
		}
		string text2 = Path.Combine(root, "FrostBeatBackup", Path.GetFileName((string)xDocument.Root.Element("backup")));
		if (Hash(text2) != (string)xDocument.Root.Element("original"))
		{
			throw new Exception("原始备份校验失败，已取消恢复。");
		}
		string text3 = text + ".frostbeat-restore";
		File.Copy(text2, text3, overwrite: true);
		File.Replace(text3, text, null);
		string text4 = Path.Combine(Game.Managed(root), "FrostBeat.Bridge.dll");
		if (File.Exists(text4) && Hash(text4) == (string)xDocument.Root.Element("bridge"))
		{
			File.Delete(text4);
		}
		File.Delete(Record(root));
	}
}
public sealed class Options
{
	public double Offset;

	public double Error;

	public double Press = 35.0;

	public double RollSpeed = 4.0;

	public double DelayFullSpeed = 10.0;

	public ushort[] Keys = new ushort[1] { 32 };

	public bool DryRun;

	public bool Humanize = true;

	public bool ScaleDelay = true;
}
public sealed class Reader : IDisposable
{
	private MemoryMappedFile map;

	private MemoryMappedViewAccessor view;

	private int pid;

	private int lastVersion = -1;

	private Beat[] beats = new Beat[0];

	public Snapshot Read(int processId)
	{
		if (pid != processId)
		{
			Dispose();
			pid = processId;
			lastVersion = -1;
		}
		if (processId <= 0)
		{
			return null;
		}
		if (map == null)
		{
			try
			{
				map = MemoryMappedFile.OpenExisting(Wire.Name(pid), MemoryMappedFileRights.Read);
				view = map.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);
			}
			catch (FileNotFoundException)
			{
				return null;
			}
			// 崩溃修复：其他映射异常（被释放/权限）也安全返回 null，不逃逸到 worker 线程。
			catch
			{
				Dispose();
				return null;
			}
		}
		// 崩溃修复：整个读取体用防御包裹，任何共享内存读取异常（撕裂/截断/已关闭）都安全返回 null。
		try
		{
			for (int i = 0; i < 3; i++)
			{
				int num = view.ReadInt32(0L);
				if (((uint)num & (true ? 1u : 0u)) != 0)
				{
					continue;
				}
				if (view.ReadInt32(4L) != 1178751538 || view.ReadInt32(8L) != pid)
				{
					return null;
				}
				Snapshot snapshot = new Snapshot();
				snapshot.Pid = pid;
				snapshot.Flags = view.ReadInt32(12L);
				snapshot.Epoch = view.ReadInt32(16L);
				snapshot.Version = view.ReadInt32(20L);
				snapshot.Current = view.ReadInt32(24L);
				snapshot.Total = view.ReadInt32(28L);
				snapshot.Tick = view.ReadInt64(32L);
				snapshot.Song = view.ReadDouble(40L);
				snapshot.Pitch = view.ReadDouble(48L);
				snapshot.GameState = view.ReadInt32(60L);
				Snapshot snapshot2 = snapshot;
				int num2 = view.ReadInt32(56L);
				if (num2 < 0 || num2 > 87338 || snapshot2.Pitch <= 0.0 || double.IsNaN(snapshot2.Song) || double.IsInfinity(snapshot2.Song))
				{
					return null;
				}
				snapshot2.Title = Text(64, 448);
				snapshot2.Reason = Text(512, 448);
				Beat[] array = beats;
				if (snapshot2.Version != lastVersion)
				{
					array = new Beat[num2];
					for (int j = 0; j < num2; j++)
					{
						long num3 = 1024 + j * 24;
						array[j] = new Beat
						{
							Time = view.ReadDouble(num3),
							Seq = view.ReadInt32(num3 + 8),
							Kind = view.ReadInt32(num3 + 12)
						};
					}
				}
				if (view.ReadInt32(0L) == num)
				{
					lastVersion = snapshot2.Version;
					beats = array;
					snapshot2.Beats = beats;
					return snapshot2;
				}
			}
		}
		catch
		{
			Dispose();
		}
		return null;
	}

	private string Text(int at, int capacity)
	{
		try
		{
			int num = view.ReadInt32(at);
			if (num < 0 || num > capacity - 4)
			{
				return "";
			}
			byte[] array = new byte[num];
			view.ReadArray(at + 4, array, 0, num);
			return Encoding.UTF8.GetString(array);
		}
		catch
		{
			return "";
		}
	}

	public void Dispose()
	{
		try
		{
			if (view != null)
			{
				view.Dispose();
			}
		}
		catch
		{
		}
		try
		{
			if (map != null)
			{
				map.Dispose();
			}
		}
		catch
		{
		}
		view = null;
		map = null;
	}
}
public sealed class Engine : IDisposable
{
	private readonly Thread worker;

	private readonly Reader reader = new Reader();

	private readonly object inputLock = new object();

	private readonly Random random = new Random();

	private readonly KeyboardOutput output = new KeyboardOutput(Native.Key);

	private volatile bool running = true;

	private volatile bool armed;

	private volatile int pid;

	private volatile Options options = new Options();

	private volatile Snapshot snapshot;

	private string status = "检测游戏进程中";

	private int sent;

	private double lastError;

	private volatile Gesture gesture;

	public int Pid
	{
		get
		{
			return pid;
		}
		set
		{
			pid = value;
		}
	}

	public Snapshot State => snapshot;

	public bool Armed => armed;

	public string Status => status;

	public int Sent => Volatile.Read(ref sent);

	public double LastError => Interlocked.CompareExchange(ref lastError, 0.0, 0.0);

	public Gesture LastGesture => gesture;

	public ushort[] HeldKeys
	{
		get
		{
			lock (inputLock)
			{
				ushort[] array = new ushort[output.Keys.Count];
				output.Keys.CopyTo(array, 0);
				return array;
			}
		}
	}

	public Engine()
	{
		// 崩溃修复：从 Highest 降为 AboveNormal，避免调度线程抢占系统资源导致整机卡顿。
		worker = new Thread(Loop)
		{
			IsBackground = true,
			Priority = ThreadPriority.AboveNormal,
			Name = "FrostBeat scheduler"
		};
		worker.Start();
	}

	public void Arm(Options o)
	{
		lock (inputLock)
		{
			options = o;
			sent = 0;
			armed = true;
			status = (o.DryRun ? "等待谱面开始 · 演练" : "等待谱面开始");
		}
	}

	public void Stop(string message)
	{
		lock (inputLock)
		{
			armed = false;
			ReleaseAll();
			status = message;
		}
	}

	public static double CurrentSong(Snapshot s, long now)
	{
		return s.Song + (double)(now - s.Tick) / (double)Clock.Frequency * s.Pitch;
	}

	public static bool Fresh(Snapshot s, long now)
	{
		if (s == null)
		{
			return false;
		}
		double num = (double)(now - s.Tick) / (double)Clock.Frequency;
		if (num >= -0.002)
		{
			return num < 0.15;
		}
		return false;
	}

	public static double TargetSeconds(Beat b, Options o, double jitter)
	{
		return o.Offset / 1000.0 + jitter / 1000.0;
	}

	public static double GestureInterval(Beat current, Gesture previous, double pitch, double upcoming)
	{
		if (previous != null)
		{
			return Math.Max(0.0, (current.Time - previous.SongTime) / pitch * 1000.0);
		}
		return upcoming;
	}

	private void Loop()
	{
		int num = -1;
		int num2 = -1;
		int num3 = -1;
		int num4 = 0;
		bool flag = false;
		bool flag2 = false;
		PlannedBeat[] array = new PlannedBeat[0];
		Humanizer humanizer = new Humanizer(random);
		bool flag3 = false;
		try
		{
			while (running)
			{
				Snapshot snapshot = null;
				try
				{
					snapshot = reader.Read(pid);
				}
				catch
				{
					reader.Dispose();
				}
				if (snapshot == null && this.snapshot != null && this.snapshot.Pid == pid)
				{
					snapshot = this.snapshot;
				}
				this.snapshot = snapshot;
				long now = Clock.Now;
				bool flag4 = Fresh(snapshot, now);
				bool flag5 = armed && flag4 && snapshot.Playing && snapshot.Focused && Native.Foreground(pid);
				if (armed && ((uint)Native.GetAsyncKeyState(27) & 0x8000u) != 0)
				{
					Stop("已停止 · Esc");
				}
				if (!armed)
				{
					flag5 = false;
				}
				if (armed && flag2 && !flag5)
				{
					Stop((flag4 && ((uint)snapshot.Flags & 2u) != 0) ? "已停止 · 游戏暂停，请按 F8 重新待命" : ((flag4 && !Native.Foreground(pid)) ? "已停止 · 游戏失去焦点" : "已停止 · 关卡结束或读取中断"));
					flag5 = false;
				}
				if (!flag5)
				{
					lock (inputLock)
					{
						ReleaseAll();
					}
					if (flag3)
					{
						Native.timeEndPeriod(1u);
						flag3 = false;
					}
					if (armed)
					{
						status = ((pid <= 0) ? "等待游戏启动" : ((snapshot == null) ? "等待读取模块连接，请安装或更新模块" : ((!flag4) ? "读取状态已过期，请重新启动游戏" : ((((uint)snapshot.Flags & 8u) != 0) ? snapshot.Reason : ((((uint)snapshot.Flags & 2u) != 0) ? "已待命 · 等待游戏继续" : ((snapshot.GameState == 5 || snapshot.GameState == 6) ? "已待命 · 关卡失败，请重新开始" : "已待命 · 请进入谱面并手动按首键"))))));
					}
					flag = armed;
					flag2 = false;
					Thread.Sleep(10);
					continue;
				}
				if (!flag3)
				{
					Native.timeBeginPeriod(1u);
					flag3 = true;
				}
				double num5 = CurrentSong(snapshot, now);
				Options options = this.options;
				if (!flag2 || !flag || num != snapshot.Version || num2 != snapshot.Epoch || num3 != snapshot.Pid)
				{
					lock (inputLock)
					{
						ReleaseAll();
					}
					num = snapshot.Version;
					num2 = snapshot.Epoch;
					num3 = snapshot.Pid;
					num4 = 0;
					humanizer.Reset();
					gesture = null;
					for (; num4 < snapshot.Beats.Length && (snapshot.Beats[num4].Time < num5 - 0.003 || snapshot.Beats[num4].Seq <= snapshot.Current); num4++)
					{
					}
					array = TimingPlan.Build(snapshot.Beats, num4, snapshot.Pitch, options, humanizer, num5);
					num4 = 0;
					status = (options.DryRun ? "演练中 · 不发送按键" : (options.Humanize ? "击键中 · 真人手法" : "击键中 · 顺序轮换"));
				}
				now = Clock.Now;
				num5 = CurrentSong(snapshot, now);
				flag = armed;
				flag2 = true;
				lock (inputLock)
				{
					if (!armed)
					{
						ReleaseAll();
						continue;
					}
					output.ReleaseDue(now);
					if (num4 < array.Length)
					{
						PlannedBeat plannedBeat = array[num4];
						double num6 = (plannedBeat.Target - num5) / snapshot.Pitch;
						if (num6 <= 0.0)
						{
							if (num6 < -0.05 && !plannedBeat.AlreadyDue)
							{
								Stop("已停止 · 调度延迟超过 50 ms，请降低后台负载");
								continue;
							}
							if (!Native.Foreground(snapshot.Pid))
							{
								Stop("已停止 · 游戏失去焦点");
								continue;
							}
							try
							{
								Dispatch(plannedBeat, options, humanizer, now);
							}
							catch (Exception ex)
							{
								Stop("已停止 · " + ex.Message);
								goto end_IL_0371;
							}
							Interlocked.Exchange(ref lastError, (0.0 - num6) * 1000.0);
							Interlocked.Increment(ref sent);
							num4++;
							goto IL_047a;
						}
						goto IL_047a;
					}
					goto IL_047a;
					end_IL_0371:;
				}
				continue;
				IL_047a:
				Thread.Sleep(1);
			}
		}
		catch (Exception ex2)
		{
			Stop("已停止 · " + ex2.Message);
		}
		finally
		{
			if (flag3)
			{
				Native.timeEndPeriod(1u);
			}
			lock (inputLock)
			{
				ReleaseAll();
			}
			reader.Dispose();
		}
	}

	private void Dispatch(PlannedBeat target, Options o, Humanizer human, long now)
	{
		Beat beat = target.Beat;
		if (beat.Kind == 2)
		{
			output.ReleaseHolds(target.HoldGroup);
			return;
		}
		int hand;
		ushort key = human.Key(o, output.Keys, target.Interval, out hand);
		double num = human.Press(o);
		now = Clock.Now;
		output.Down(key, num, now, beat.Kind == 1, o.DryRun, target.HoldGroup);
		gesture = new Gesture
		{
			Key = key,
			Hand = hand,
			Press = ((beat.Kind == 1) ? double.PositiveInfinity : num),
			Jitter = target.Jitter,
			DelayLimit = target.DelayLimit,
			DelayRate = target.DelayRate,
			Tick = now,
			SongTime = beat.Time,
			Rate = human.Rate,
			KeyCount = human.ActiveKeyCount,
			ActiveKeys = (o.Humanize ? human.ActiveKeys(o) : string.Join(",", Array.ConvertAll(o.Keys, Humanizer.KeyName))),
			Borrowed = human.Borrowed
		};
	}

	private void ReleaseAll()
	{
		output.ReleaseAll();
	}

	public void Dispose()
	{
		Stop("已退出");
		running = false;
		worker.Join(1000);
	}
}
public sealed class Gesture
{
	public ushort Key;

	public int Hand;

	public int KeyCount;

	public double Press;

	public double Jitter;

	public double Rate;

	public double SongTime;

	public double DelayLimit;

	public double DelayRate;

	public long Tick;

	public string ActiveKeys;

	public bool Borrowed;
}
public sealed class Humanizer
{
	private readonly Random random;

	private int roundRobin;

	private int cursor;

	private int activeCount = 1;

	private int slowStreak;

	private double rate;

	private bool observed;

	private bool normalMode;

	private int[][] stages;

	private int[] priority;

	private ushort[] configured;

	public int ActiveKeyCount => activeCount;

	public double Rate => rate;

	public bool Borrowed { get; private set; }

	public Humanizer(Random random)
	{
		this.random = random;
	}

	public void Reset()
	{
		roundRobin = (cursor = (slowStreak = 0));
		activeCount = 1;
		rate = 0.0;
		observed = (normalMode = false);
		configured = null;
		stages = null;
		priority = null;
		Borrowed = false;
	}

	public static string KeyName(ushort key)
	{
		return Native.KeyLabel(key);
	}

	public string ActiveKeys(Options o)
	{
		if (stages != null)
		{
			return string.Join(",", Array.ConvertAll(stages[activeCount - 1], (int at) => KeyName(o.Keys[at])));
		}
		return "";
	}

	public static int Hand(ushort key)
	{
		if ("12345QWERTASDFGZXCVB".IndexOf((char)key) >= 0)
		{
			return 0;
		}
		if ("67890YUIOPHJKLNM".IndexOf((char)key) >= 0)
		{
			return 1;
		}
		return -1;
	}

	public static double Gap(Beat a, Beat b, double pitch)
	{
		if (a != null && b != null)
		{
			return Math.Max(0.0, (b.Time - a.Time) / pitch * 1000.0);
		}
		return double.PositiveInfinity;
	}

	public double Jitter(double limit)
	{
		return (random.NextDouble() * 2.0 - 1.0) * limit;
	}

	private void Density(Options o, double intervalMs)
	{
		double num = (double.IsPositiveInfinity(intervalMs) ? 0.0 : (1000.0 / Math.Max(1.0, intervalMs)));
		if (!observed)
		{
			rate = num;
			observed = true;
			return;
		}
		if (intervalMs >= 1600.0 / o.RollSpeed)
		{
			rate = num;
			activeCount = 1;
			cursor = (slowStreak = 0);
			return;
		}
		rate = 0.55 * rate + 0.45 * num;
		if (activeCount < o.Keys.Length && rate + 1E-08 >= (double)activeCount * o.RollSpeed)
		{
			activeCount++;
			slowStreak = (cursor = 0);
		}
		else if (activeCount > 1 && rate < (double)(activeCount - 1) * o.RollSpeed * 0.82)
		{
			if (++slowStreak >= 3)
			{
				activeCount--;
				slowStreak = (cursor = 0);
			}
		}
		else
		{
			slowStreak = 0;
		}
		cursor %= activeCount;
	}

	public ushort Key(Options o, ICollection<ushort> held, double intervalMs, out int hand)
	{
		Borrowed = false;
		if (!o.Humanize)
		{
			normalMode = true;
			activeCount = o.Keys.Length;
			for (int i = 0; i < o.Keys.Length; i++)
			{
				ushort num = o.Keys[roundRobin++ % o.Keys.Length];
				if (!held.Contains(num))
				{
					hand = Hand(num);
					return num;
				}
			}
			throw new InvalidOperationException("所有击键仍在按住，请缩短按住时长或增加可用按键。");
		}
		bool flag = normalMode || configured == null || configured.Length != o.Keys.Length;
		if (!flag)
		{
			for (int j = 0; j < configured.Length; j++)
			{
				if (configured[j] != o.Keys[j])
				{
					flag = true;
					break;
				}
			}
		}
		if (flag)
		{
			HandProfile handProfile = HandProfile.Build(o);
			Reset();
			configured = (ushort[])o.Keys.Clone();
			stages = handProfile.Stages;
			priority = handProfile.Priority;
		}
		Density(o, intervalMs);
		int[] array = stages[activeCount - 1];
		for (int k = 0; k < array.Length; k++)
		{
			int num2 = (cursor + k) % array.Length;
			ushort num3 = o.Keys[array[num2]];
			if (!held.Contains(num3))
			{
				cursor = (num2 + 1) % array.Length;
				hand = Hand(num3);
				return num3;
			}
		}
		for (int l = activeCount; l < priority.Length; l++)
		{
			ushort num4 = o.Keys[priority[l]];
			if (!held.Contains(num4))
			{
				Borrowed = true;
				hand = Hand(num4);
				return num4;
			}
		}
		throw new InvalidOperationException("所有击键仍在按住，请增加可用按键或缩短轻点时长。");
	}

	public double Press(Options o)
	{
		return o.Press;
	}
}
public sealed class HandProfile
{
	public int[] Priority;

	public int[] Wheel;

	public int[][] Stages;

	public ushort Main;

	public ushort Aux;

	public static HandProfile Build(Options o)
	{
		if (o.Keys == null || o.Keys.Length < 1 || o.Keys.Length > 32)
		{
			throw new ArgumentException("请设置 1–32 个击键。");
		}
		HashSet<ushort> hashSet = new HashSet<ushort>(o.Keys);
		if (hashSet.Count != o.Keys.Length)
		{
			throw new ArgumentException("可用按键不能重复。");
		}
		int num = o.Keys.Length / 2;
		List<int> list = new List<int>();
		list.Add(num);
		List<int> list2 = list;
		int num2 = 1;
		while (list2.Count < o.Keys.Length)
		{
			if (num - num2 >= 0)
			{
				list2.Add(num - num2);
			}
			if (num + num2 < o.Keys.Length)
			{
				list2.Add(num + num2);
			}
			num2++;
		}
		bool[] array = new bool[o.Keys.Length];
		int[][] array2 = new int[o.Keys.Length][];
		for (int i = 1; i <= o.Keys.Length; i++)
		{
			array[list2[i - 1]] = true;
			List<int> list3 = new List<int>();
			for (int num3 = o.Keys.Length - 1; num3 >= num; num3--)
			{
				if (array[num3])
				{
					list3.Add(num3);
				}
			}
			for (int j = 0; j < num; j++)
			{
				if (array[j])
				{
					list3.Add(j);
				}
			}
			array2[i - 1] = list3.ToArray();
		}
		HandProfile handProfile = new HandProfile();
		handProfile.Priority = list2.ToArray();
		handProfile.Wheel = array2[array2.Length - 1];
		handProfile.Stages = array2;
		handProfile.Main = o.Keys[num];
		handProfile.Aux = (ushort)((list2.Count > 1) ? o.Keys[list2[1]] : 0);
		return handProfile;
	}

	public static string Names(ushort[] keys, int[] order)
	{
		return string.Join(" → ", Array.ConvertAll(order, (int i) => Humanizer.KeyName(keys[i])));
	}

	public string Preview(ushort[] keys)
	{
		List<string> list = new List<string>();
		for (int i = 0; i < Stages.Length; i++)
		{
			list.Add(i + 1 + " 键  " + string.Join(" ", Array.ConvertAll(Stages[i], (int at) => Humanizer.KeyName(keys[at]))));
		}
		return string.Join("\n", list);
	}
}
public sealed class PlannedBeat
{
	public Beat Beat;

	public double Target;

	public double Jitter;

	public double Interval;

	public double DelayLimit;

	public double DelayRate;

	public int Index;

	public int HoldGroup;

	public bool AlreadyDue;
}
public static class DelayProfile
{
	public static double Rate(double interval)
	{
		if (!double.IsPositiveInfinity(interval) && !double.IsNaN(interval))
		{
			return 1000.0 / Math.Max(1.0, interval);
		}
		return 0.0;
	}

	public static double Limit(Options options, double interval)
	{
		double num = Math.Max(0.0, options.Error);
		if (!options.ScaleDelay)
		{
			return num;
		}
		return num * Math.Min(1.0, Rate(interval) / Math.Max(0.5, options.DelayFullSpeed));
	}
}
public static class TimingPlan
{
	public static PlannedBeat[] Build(Beat[] beats, int first, double pitch, Options options, Humanizer human, double startSong = double.NegativeInfinity)
	{
		List<PlannedBeat> list = new List<PlannedBeat>();
		Beat beat = null;
		double num = 0.0;
		double num2 = 0.0;
		double num3 = 0.0;
		bool flag = false;
		int num4 = 0;
		for (int i = 0; i < beats.Length; i++)
		{
			Beat beat2 = beats[i];
			double num5 = 0.0;
			if (i >= first)
			{
				double interval = ((beat == null) ? double.PositiveInfinity : Humanizer.Gap(beat, beat2, pitch));
				if (beat == null)
				{
					for (int j = i + 1; j < beats.Length; j++)
					{
						if (beats[j].Kind != 2)
						{
							interval = Humanizer.Gap(beat2, beats[j], pitch);
							break;
						}
					}
				}
				double num6 = DelayProfile.Limit(options, interval);
				double num7 = DelayProfile.Rate(interval);
				if (beat2.Kind == 2 && flag)
				{
					num5 = num;
					num6 = num2;
					num7 = num3;
					flag = false;
				}
				else if (beat2.Kind == 1)
				{
					if (!flag)
					{
						num2 = num6;
						num3 = num7;
						num = human.Jitter(num6);
						flag = true;
						num4++;
					}
					num5 = num;
					num6 = num2;
					num7 = num3;
				}
				else
				{
					num5 = human.Jitter(num6);
					if (beat2.Kind == 2)
					{
						flag = false;
					}
				}
				double num8 = beat2.Time + (options.Offset + num5) * pitch / 1000.0;
				list.Add(new PlannedBeat
				{
					Beat = beat2,
					Index = i,
					HoldGroup = ((beat2.Kind != 0) ? num4 : 0),
					Jitter = num5,
					DelayLimit = num6,
					DelayRate = num7,
					Target = num8,
					Interval = interval,
					AlreadyDue = (num8 < startSong)
				});
			}
			if (beat2.Kind != 2)
			{
				beat = beat2;
			}
		}
		list.Sort(delegate(PlannedBeat a, PlannedBeat b)
		{
			int num9 = a.Target.CompareTo(b.Target);
			return (num9 != 0) ? num9 : a.Index.CompareTo(b.Index);
		});
		return list.ToArray();
	}
}
public sealed class KeyboardOutput
{
	private sealed class Held
	{
		public long Release;

		public bool Dry;

		public int Group;
	}

	private readonly Dictionary<ushort, Held> held = new Dictionary<ushort, Held>();

	private readonly Action<ushort, bool> send;

	public ICollection<ushort> Keys => held.Keys;

	public KeyboardOutput(Action<ushort, bool> send)
	{
		this.send = send;
	}

	public void Down(ushort key, double milliseconds, long now, bool hold, bool dry, int group = 0)
	{
		if (held.ContainsKey(key))
		{
			throw new InvalidOperationException("按键仍在按住，无法提前重按。请缩短按住时长或增加按键。");
		}
		if (!dry)
		{
			send(key, arg2: false);
		}
		held.Add(key, new Held
		{
			Release = (hold ? long.MaxValue : (now + (long)(milliseconds / 1000.0 * (double)Clock.Frequency))),
			Dry = dry,
			Group = group
		});
	}

	private void Up(ushort key)
	{
		Held held = this.held[key];
		if (!held.Dry)
		{
			send(key, arg2: true);
		}
		this.held.Remove(key);
	}

	public void ReleaseDue(long now)
	{
		List<ushort> list = new List<ushort>();
		foreach (KeyValuePair<ushort, Held> item in held)
		{
			if (item.Value.Release != long.MaxValue && item.Value.Release <= now)
			{
				list.Add(item.Key);
			}
		}
		foreach (ushort item2 in list)
		{
			Up(item2);
		}
	}

	public void ReleaseHolds(int group)
	{
		List<ushort> list = new List<ushort>();
		foreach (KeyValuePair<ushort, Held> item in held)
		{
			if (item.Value.Release == long.MaxValue && item.Value.Group == group)
			{
				list.Add(item.Key);
			}
		}
		foreach (ushort item2 in list)
		{
			Up(item2);
		}
	}

	public void ReleaseAll()
	{
		List<ushort> list = new List<ushort>(held.Keys);
		foreach (ushort item in list)
		{
			try
			{
				Up(item);
			}
			catch
			{
			}
		}
		held.Clear();
	}
}
public static class InputPreferences
{
	public static string Duration(XElement settings)
	{
		XElement xElement = settings.Element("KeyDurationBox");
		if (xElement != null)
		{
			return (string)xElement;
		}
		bool flag = true;
		if (bool.TryParse((string)settings.Element("HumanizeBox"), out var result))
		{
			flag = result;
		}
		XElement xElement2 = (flag ? settings.Element("HumanPressBox") : null) ?? settings.Element("PressBox");
		if (xElement2 != null)
		{
			return (string)xElement2;
		}
		return "35";
	}
}
public sealed class Overlay
{
	private readonly Window window;

	private bool applying;

	public bool Expanded;

	public double Scale = 1.0;

	public double CompactWidth = 350.0;

	public double CompactHeight = 248.0;

	public double ExpandedWidth = 440.0;

	public double ExpandedHeight = 740.0;

	public Overlay(Window window)
	{
		Overlay overlay = this;
		this.window = window;
		FrameworkElement frameworkElement = Get<FrameworkElement>("DragHeader");
		MouseButtonEventHandler value = delegate(object s, MouseButtonEventArgs e)
		{
			if (!(e.OriginalSource is System.Windows.Controls.Button) && !AncestorButton(e.OriginalSource as DependencyObject) && e.ButtonState == MouseButtonState.Pressed)
			{
				window.DragMove();
			}
		};
		frameworkElement.MouseLeftButtonDown += value;
		Get<Thumb>("ResizeGrip").DragDelta += delegate(object s, DragDeltaEventArgs e)
		{
			window.Width = Math.Max(window.MinWidth, window.Width + e.HorizontalChange);
			window.Height = Math.Max(window.MinHeight, window.Height + e.VerticalChange);
		};
		window.SizeChanged += delegate
		{
			if (!applying)
			{
				Remember();
			}
		};
		Get<System.Windows.Controls.Button>("MinimizeButton").Click += delegate
		{
			window.WindowState = WindowState.Minimized;
		};
		Get<System.Windows.Controls.Button>("CloseButton").Click += delegate
		{
			window.Close();
		};
	}

	private static bool AncestorButton(DependencyObject d)
	{
		while (d != null)
		{
			if (d is System.Windows.Controls.Button)
			{
				return true;
			}
			d = VisualTreeHelper.GetParent(d);
		}
		return false;
	}

	private T Get<T>(string n) where T : class
	{
		return window.FindName(n) as T;
	}

	public void Remember()
	{
		if (Expanded)
		{
			ExpandedWidth = window.Width / Scale;
			ExpandedHeight = window.Height / Scale;
		}
		else
		{
			CompactWidth = window.Width / Scale;
			CompactHeight = window.Height / Scale;
		}
	}

	public void Apply()
	{
		applying = true;
		Get<ScrollViewer>("SettingsPanel").Visibility = ((!Expanded) ? Visibility.Collapsed : Visibility.Visible);
		Get<System.Windows.Controls.Button>("ExpandButton").Content = (Expanded ? "收起 ▴" : "展开 ▾");
		Get<TextBlock>("FooterText").Text = (Expanded ? "调整完成后收起，再切回游戏" : "拖动标题移动 · Ctrl+F10 展开");
		ScaleTransform scaleTransform = Get<ScaleTransform>("UiScale");
		double scaleX = (scaleTransform.ScaleY = Scale);
		scaleTransform.ScaleX = scaleX;
		window.MinWidth = (double)(Expanded ? 380 : 300) * Scale;
		window.MinHeight = (double)(Expanded ? 500 : 230) * Scale;
		Rect workArea = SystemParameters.WorkArea;
		window.Width = Math.Max(window.MinWidth, Math.Min((Expanded ? ExpandedWidth : CompactWidth) * Scale, workArea.Width));
		window.Height = Math.Max(window.MinHeight, Math.Min((Expanded ? ExpandedHeight : CompactHeight) * Scale, workArea.Height));
		if (!double.IsNaN(window.Left))
		{
			window.Left = Math.Max(workArea.Left, Math.Min(window.Left, workArea.Right - window.Width));
		}
		if (!double.IsNaN(window.Top))
		{
			window.Top = Math.Max(workArea.Top, Math.Min(window.Top, workArea.Bottom - window.Height));
		}
		applying = false;
	}

	public void SetExpanded(bool expanded)
	{
		Remember();
		Expanded = expanded;
		Apply();
	}

	public void SetScale(double value)
	{
		Remember();
		Scale = Math.Max(0.8, Math.Min(1.4, value));
		Apply();
	}
}
public static class Native
{
	private struct INPUT
	{
		public uint type;

		public UNION u;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct UNION
	{
		[FieldOffset(0)]
		public KEYBDINPUT kb;

		[FieldOffset(0)]
		public MOUSEINPUT mouse;
	}

	private struct KEYBDINPUT
	{
		public ushort vk;

		public ushort scan;

		public uint flags;

		public uint time;

		public UIntPtr extra;
	}

	private struct MOUSEINPUT
	{
		public int x;

		public int y;

		public uint data;

		public uint flags;

		public uint time;

		public UIntPtr extra;
	}

	public const string UnsupportedMessage = "不支持输入以下按键：Esc、Win、F1~F12、PrtSc、Delete、CapsLK、NumLock、Copilot";

	private static readonly Dictionary<string, ushort> NamedKeys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
	{
		{ "BACKSPACE", 8 },
		{ "BACK", 8 },
		{ "BKSP", 8 },
		{ "TAB", 9 },
		{ "ENTER", 13 },
		{ "RETURN", 13 },
		{ "SHIFT", 16 },
		{ "CTRL", 17 },
		{ "CONTROL", 17 },
		{ "ALT", 18 },
		{ "MENU", 18 },
		{ "PAUSE", 19 },
		{ "SPACE", 32 },
		{ "空格", 32 },
		{ "PAGEUP", 33 },
		{ "PGUP", 33 },
		{ "PAGEDOWN", 34 },
		{ "PGDN", 34 },
		{ "END", 35 },
		{ "HOME", 36 },
		{ "LEFT", 37 },
		{ "UP", 38 },
		{ "RIGHT", 39 },
		{ "DOWN", 40 },
		{ "INSERT", 45 },
		{ "INS", 45 },
		{ "APPS", 93 },
		{ "APPLICATION", 93 },
		{ "SCROLLLOCK", 145 },
		{ "SCROLL", 145 },
		{ "COMMA", 188 },
		{ "SEMICOLON", 186 },
		{ ";", 186 },
		{ "EQUALS", 187 },
		{ "EQUAL", 187 },
		{ "=", 187 },
		{ "MINUS", 189 },
		{ "-", 189 },
		{ "PERIOD", 190 },
		{ "DOT", 190 },
		{ ".", 190 },
		{ "SLASH", 191 },
		{ "/", 191 },
		{ "BACKQUOTE", 192 },
		{ "GRAVE", 192 },
		{ "`", 192 },
		{ "LBRACKET", 219 },
		{ "[", 219 },
		{ "BACKSLASH", 220 },
		{ "\\", 220 },
		{ "RBRACKET", 221 },
		{ "]", 221 },
		{ "APOSTROPHE", 222 },
		{ "QUOTE", 222 },
		{ "'", 222 },
		{ "NUMMULTIPLY", 106 },
		{ "NUMPADMULTIPLY", 106 },
		{ "NUMADD", 107 },
		{ "NUMPADADD", 107 },
		{ "NUMSUBTRACT", 109 },
		{ "NUMPADSUBTRACT", 109 },
		{ "NUMDECIMAL", 110 },
		{ "NUMPADDECIMAL", 110 },
		{ "NUMDIVIDE", 111 },
		{ "NUMPADDIVIDE", 111 }
	};

	public static int InputSize => Marshal.SizeOf(typeof(INPUT));

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint count, INPUT[] input, int size);

	[DllImport("user32.dll")]
	private static extern uint MapVirtualKey(uint code, uint type);

	[DllImport("user32.dll")]
	public static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

	[DllImport("user32.dll")]
	public static extern short GetAsyncKeyState(int key);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);

	[DllImport("user32.dll")]
	public static extern bool UnregisterHotKey(IntPtr h, int id);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

	public static void OverlayFocus(IntPtr h, bool compact)
	{
		if (!(h == IntPtr.Zero))
		{
			long num = GetWindowLongPtr(h, -20).ToInt64();
			SetWindowLongPtr(h, -20, new IntPtr(compact ? (num | 0x8000000) : (num & -134217729)));
		}
	}

	[DllImport("winmm.dll")]
	public static extern uint timeBeginPeriod(uint period);

	[DllImport("winmm.dll")]
	public static extern uint timeEndPeriod(uint period);

	public static bool Foreground(int pid)
	{
		GetWindowThreadProcessId(GetForegroundWindow(), out var pid2);
		return pid2 == pid;
	}

	public static void Key(ushort key, bool up)
	{
		INPUT iNPUT = default(INPUT);
		iNPUT.type = 1u;
		iNPUT.u.kb.scan = (ushort)MapVirtualKey(key, 0u);
		uint num = 8u;
		if (key == 33 || key == 34 || key == 35 || key == 36 || key == 37 || key == 38 || key == 39 || key == 40 || key == 45 || key == 91 || key == 92 || key == 93 || key == 106 || key == 107 || key == 109 || key == 110 || key == 111 || key == 145)
		{
			num |= 1u;
		}
		if (up)
		{
			num |= 2u;
		}
		iNPUT.u.kb.flags = num;
		if (iNPUT.u.kb.scan == 0)
		{
			iNPUT.u.kb.vk = key;
			iNPUT.u.kb.flags = (up ? 2u : 0u);
		}
		uint num2 = SendInput(1u, new INPUT[1] { iNPUT }, InputSize);
		if (num2 != 1)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "键盘输入被系统拒绝；请检查辅助工具与游戏权限是否一致。");
		}
	}

	public static ushort NormalizeKey(ushort code)
	{
		switch (code)
		{
		case 160:
		case 161:
			return 16;
		case 162:
		case 163:
			return 17;
		case 164:
		case 165:
			return 18;
		default:
			return code;
		}
	}

	public static bool IsUnsupportedKey(ushort code)
	{
		code = NormalizeKey(code);
		if (code != 27 && code != 91 && code != 92 && code != 44 && code != 46 && code != 20 && code != 144 && code != 227 && code != 231)
		{
			if (code >= 112)
			{
				return code <= 123;
			}
			return false;
		}
		return true;
	}

	public static string KeyLabel(ushort rawCode)
	{
		ushort num = NormalizeKey(rawCode);
		switch (num)
		{
		case 8:
			return "Backspace";
		case 9:
			return "Tab";
		case 13:
			return "Enter";
		case 16:
			return "Shift";
		case 17:
			return "Ctrl";
		case 18:
			return "Alt";
		case 19:
			return "Pause";
		case 32:
			return "SPACE";
		case 33:
			return "PageUp";
		case 34:
			return "PageDown";
		case 35:
			return "End";
		case 36:
			return "Home";
		case 37:
			return "Left";
		case 38:
			return "Up";
		case 39:
			return "Right";
		case 40:
			return "Down";
		case 45:
			return "Insert";
		case 93:
			return "Apps";
		case 145:
			return "ScrollLock";
		case 186:
			return ";";
		case 187:
			return "=";
		case 188:
			return "Comma";
		case 189:
			return "-";
		case 190:
			return ".";
		case 191:
			return "/";
		case 192:
			return "`";
		case 219:
			return "[";
		case 220:
			return "\\";
		case 221:
			return "]";
		case 222:
			return "'";
		case 106:
			return "NumMultiply";
		case 107:
			return "NumAdd";
		case 109:
			return "NumSubtract";
		case 110:
			return "NumDecimal";
		case 111:
			return "NumDivide";
		default:
			if (num >= 96 && num <= 105)
			{
				return "Num" + (num - 96);
			}
			if (num >= 65 && num <= 90)
			{
				char c = (char)num;
				return c.ToString();
			}
			if (num >= 48 && num <= 57)
			{
				char c2 = (char)num;
				return c2.ToString();
			}
			return "VK" + num;
		}
	}

	public static ushort[] ParseKeys(string text)
	{
		List<ushort> list = new List<ushort>();
		string[] array = text.Replace('，', ',').Split(',');
		foreach (string text2 in array)
		{
			string text3 = text2.Trim().ToUpperInvariant();
			if (text3.Length == 0)
			{
				continue;
			}
			ushort value = 0;
			switch (text3)
			{
			case "ESC":
			case "ESCAPE":
			case "WIN":
			case "LWIN":
			case "RWIN":
			case "PRTSC":
			case "PRINTSCREEN":
			case "DELETE":
			case "DEL":
			case "CAPSLOCK":
			case "CAPSLK":
			case "NUMLOCK":
			case "COPILOT":
				throw new ArgumentException("不支持输入以下按键：Esc、Win、F1~F12、PrtSc、Delete、CapsLK、NumLock、Copilot");
			}
			if (text3.Length == 1 && ((text3[0] >= 'A' && text3[0] <= 'Z') || (text3[0] >= '0' && text3[0] <= '9')))
			{
				value = text3[0];
			}
			else if (!NamedKeys.TryGetValue(text3, out value))
			{
				if (text3.StartsWith("NUMPAD") && text3.Length == 7 && text3[6] >= '0' && text3[6] <= '9')
				{
					value = (ushort)(96 + text3[6] - 48);
				}
				else
				{
					if (!text3.StartsWith("NUM") || text3.Length != 4 || text3[3] < '0' || text3[3] > '9')
					{
						throw new ArgumentException("无法识别按键名称：" + text3 + "。请直接在按键框中按下要添加的实体按键。英文逗号用于分隔按键。 ");
					}
					value = (ushort)(96 + text3[3] - 48);
				}
			}
			value = NormalizeKey(value);
			if (IsUnsupportedKey(value))
			{
				throw new ArgumentException("不支持输入以下按键：Esc、Win、F1~F12、PrtSc、Delete、CapsLK、NumLock、Copilot");
			}
			if (!list.Contains(value))
			{
				list.Add(value);
			}
		}
		if (list.Count == 0 || list.Count > 32)
		{
			throw new ArgumentException("请设置 1–32 个不重复的击键。");
		}
		return list.ToArray();
	}
}
public static class Clock
{
	public static readonly long Frequency = GetFrequency();

	public static long Now
	{
		get
		{
			if (!QueryPerformanceCounter(out var value))
			{
				throw new InvalidOperationException("Windows 高精度时钟读取失败。");
			}
			return value;
		}
	}

	[DllImport("kernel32.dll")]
	private static extern bool QueryPerformanceCounter(out long value);

	[DllImport("kernel32.dll")]
	private static extern bool QueryPerformanceFrequency(out long value);

	private static long GetFrequency()
	{
		if (!QueryPerformanceFrequency(out var value) || value <= 0)
		{
			throw new InvalidOperationException("Windows 高精度时钟不可用。");
		}
		return value;
	}
}
public static class Wire
{
	public const int Magic = 1178751538;

	public const int Capacity = 2097152;

	public const int DataOffset = 1024;

	public const int EventSize = 24;

	public const int MaxEvents = 87338;

	public static string Name(int pid)
	{
		return "Local\\FrostBeat.ReadOnly." + pid;
	}
}
public sealed class Beat
{
	public double Time;

	public int Seq;

	public int Kind;
}
public sealed class FloorInfo
{
	public double Time;

	public int Seq;

	public int Hold = -1;

	public int Taps = 1;

	public bool Midspin;

	public bool Auto;

	public bool Freeroam;
}
public static class Timeline
{
	public static Beat[] Build(IList<FloorInfo> floors)
	{
		List<Beat> list = new List<Beat>();
		for (int i = 0; i < floors.Count - 1; i++)
		{
			FloorInfo floorInfo = floors[i];
			FloorInfo floorInfo2 = floors[i + 1];
			if (!floorInfo.Midspin && !floorInfo2.Auto && !double.IsNaN(floorInfo2.Time) && !double.IsInfinity(floorInfo2.Time) && !(floorInfo2.Time <= 0.0))
			{
				int kind = ((floorInfo.Hold >= 0 && floorInfo2.Hold < 0) ? 2 : ((floorInfo2.Hold >= 0) ? 1 : 0));
				list.Add(new Beat
				{
					Time = floorInfo2.Time,
					Seq = floorInfo2.Seq,
					Kind = kind
				});
			}
		}
		return list.ToArray();
	}
}
public sealed class Snapshot
{
	public int Pid;

	public int Flags;

	public int Epoch;

	public int Version;

	public int Current;

	public int Total;

	public int GameState;

	public long Tick;

	public double Song;

	public double Pitch = 1.0;

	public string Title = "";

	public string Reason = "";

	public Beat[] Beats = new Beat[0];

	public bool Playing => (Flags & 1) != 0;

	public bool Focused => (Flags & 4) != 0;
}
