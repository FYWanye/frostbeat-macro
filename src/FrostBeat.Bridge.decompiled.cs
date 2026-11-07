using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: AssemblyVersion("1.0.0.0")]
namespace FrostBeat;

public static class Bridge
{
	private static MemoryMappedFile map;

	private static MemoryMappedViewAccessor view;

	private static readonly Dictionary<string, MemberInfo> members = new Dictionary<string, MemberInfo>();

	private static object lastList;

	private static int version;

	private static int epoch;

	private static int sequence;

	private static double lastSong = -10000.0;

	private static double lastPitch;

	private static bool dirty = true;

	private static string title = "";

	private static string reason = "";

	private static int count;

	private static int total;

	private static int lastState = -1;

	private static readonly int pid = Process.GetCurrentProcess().Id;

	private static bool logged;

	private static object Read(object obj, string name)
	{
		if (obj == null)
		{
			return null;
		}
		Type type = (obj as Type) ?? obj.GetType();
		string key = type.FullName + "/" + name;
		if (!members.TryGetValue(key, out var value))
		{
			value = (MemberInfo)(((object)type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) ?? ((object)type.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)));
			members[key] = value;
		}
		object obj2 = ((obj is Type) ? null : obj);
		FieldInfo fieldInfo = value as FieldInfo;
		PropertyInfo propertyInfo = value as PropertyInfo;
		if (!(fieldInfo != null))
		{
			if (!(propertyInfo != null))
			{
				return null;
			}
			return propertyInfo.GetValue(obj2, null);
		}
		return fieldInfo.GetValue(obj2);
	}

	private static bool Bool(object obj, string n)
	{
		object obj2 = Read(obj, n);
		if (obj2 is bool)
		{
			return (bool)obj2;
		}
		return false;
	}

	private static int Int(object obj, string n)
	{
		return Convert.ToInt32(Read(obj, n));
	}

	private static double Num(object obj, string n)
	{
		return Convert.ToDouble(Read(obj, n));
	}

	public static void MarkDirty()
	{
		dirty = true;
	}

	public static void Tick(object conductor)
	{
		try
		{
			TickCore(conductor);
		}
		catch (Exception ex)
		{
			try
			{
				if (view != null)
				{
					view.Write(12L, 0);
				}
			}
			catch
			{
			}
			if (!logged)
			{
				logged = true;
				try
				{
					File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FrostBeat.bridge.log"), string.Concat(DateTime.Now, " ", ex, Environment.NewLine));
					return;
				}
				catch
				{
					return;
				}
			}
		}
	}

	private static void TickCore(object conductor)
	{
		if (map == null)
		{
			map = MemoryMappedFile.CreateOrOpen(Wire.Name(pid), 2097152L);
			view = map.CreateViewAccessor();
		}
		Assembly assembly = conductor.GetType().Assembly;
		object obj = Read(assembly.GetType("scrController"), "instance");
		object obj2 = Read(assembly.GetType("scrLevelMaker"), "instance");
		IList list = Read(obj2, "listFloors") as IList;
		int num = ((obj == null) ? (-1) : Int(obj, "state"));
		object obj3 = Read(conductor, "song");
		double num2 = ((obj3 == null) ? 1.0 : Num(obj3, "pitch"));
		double num3 = Num(conductor, "songposition_minusi");
		bool flag = Bool(obj, "paused") || Bool(obj, "audioPaused");
		bool flag2 = Bool(obj, "gameworld");
		bool flag3 = Bool(assembly.GetType("UnityEngine.Application") ?? Type.GetType("UnityEngine.Application, UnityEngine.CoreModule"), "isFocused");
		object obj4 = Read(obj, "playerOne");
		bool flag4 = obj4 == null || Bool(obj4, "alive");
		bool flag5 = Bool(obj, "coopMode");
		bool flag6 = Bool(obj4, "auto");
		bool flag7 = num == 4 && !flag && flag2 && flag4 && !flag6 && !flag5 && Bool(conductor, "hasSongStarted");
		bool flag8 = dirty || !object.ReferenceEquals(lastList, list) || Math.Abs(lastPitch - num2) > 0.0001 || (num != lastState && (num == 2 || num == 4));
		if (num3 < lastSong - 0.05 || (num == 2 && lastState != 2))
		{
			epoch++;
		}
		sequence += 2;
		view.Write(0L, sequence - 1);
		if (flag8)
		{
			List<FloorInfo> list2 = new List<FloorInfo>();
			reason = "";
			if (list != null)
			{
				foreach (object item in list)
				{
					FloorInfo floorInfo = new FloorInfo();
					floorInfo.Time = Num(item, "entryTime");
					floorInfo.Seq = Int(item, "seqID");
					floorInfo.Hold = Int(item, "holdLength");
					floorInfo.Taps = Int(item, "tapsNeeded");
					floorInfo.Midspin = Bool(item, "midSpin");
					floorInfo.Auto = Bool(item, "auto");
					floorInfo.Freeroam = Bool(item, "freeroam");
					FloorInfo floorInfo2 = floorInfo;
					list2.Add(floorInfo2);
					if (floorInfo2.Freeroam)
					{
						reason = "自由移动谱面暂不支持";
					}
					if (floorInfo2.Taps > 1)
					{
						reason = "含多击砖块，暂不支持自动输入";
					}
				}
			}
			Beat[] array = Timeline.Build(list2);
			if (array.Length > 87338)
			{
				array = new Beat[0];
				reason = "谱面超出事件容量";
			}
			count = array.Length;
			total = list2.Count;
			for (int i = 0; i < count; i++)
			{
				long num4 = 1024 + i * 24;
				view.Write(num4, array[i].Time);
				view.Write(num4 + 8, array[i].Seq);
				view.Write(num4 + 12, array[i].Kind);
			}
			object obj5 = Read(assembly.GetType("scnGame"), "instance");
			object obj6 = Read(obj5, "levelData");
			title = Convert.ToString(Read(obj6, "song"));
			if (string.IsNullOrWhiteSpace(title))
			{
				title = Convert.ToString(Read(obj, "levelName"));
			}
			if (string.IsNullOrWhiteSpace(title))
			{
				title = Convert.ToString(Read(assembly.GetType("ADOBase"), "currentLevel"));
			}
			if (string.IsNullOrWhiteSpace(title))
			{
				title = "等待打开谱面";
			}
			lastList = list;
			lastPitch = num2;
			dirty = false;
			version++;
		}
		string text = reason;
		if (flag5)
		{
			text = "双人模式暂不支持";
		}
		if (flag6)
		{
			text = "请关闭游戏自带自动播放";
		}
		if (text.Length > 0)
		{
			flag7 = false;
		}
		int value = (flag7 ? 1 : 0) | (flag ? 2 : 0) | (flag3 ? 4 : 0) | ((text.Length > 0) ? 8 : 0) | (flag2 ? 16 : 0);
		view.Write(4L, 1178751538);
		view.Write(8L, pid);
		view.Write(12L, value);
		view.Write(16L, epoch);
		view.Write(20L, version);
		view.Write(24L, Int(obj, "currentSeqID"));
		view.Write(28L, total);
		view.Write(32L, Clock.Now);
		view.Write(40L, num3);
		view.Write(48L, num2);
		view.Write(56L, count);
		view.Write(60L, num);
		WriteText(64, title, 448);
		WriteText(512, text, 448);
		view.Write(0L, sequence);
		lastSong = num3;
		lastState = num;
	}

	private static void WriteText(int at, string s, int capacity)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
		int value = Math.Min(bytes.Length, capacity - 4);
		view.Write(at, value);
		view.WriteArray(at + 4, bytes, 0, value);
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
