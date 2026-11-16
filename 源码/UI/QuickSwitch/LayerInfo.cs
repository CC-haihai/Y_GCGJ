using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AutoLayer.UI.QuickSwitch;

public class LayerInfo : INotifyPropertyChanged, IComparable<LayerInfo>
{
	private bool _isHighlighted;

	private bool _isRecent;

	public string Name { get; set; } = string.Empty;

	public bool IsHeader { get; set; }

	private System.Drawing.Color _acadColor;
	private SolidColorBrush _colorBrush;

	public System.Drawing.Color AcadColor
	{
		get { return _acadColor; }
		set { _acadColor = value; _colorBrush = null; }
	}

	/// <summary>
	/// 列表行左边那个色块。
	///
	/// 原来每次 get 都 new 一个 SolidColorBrush —— 这是绑定属性，列表滚动 / 重新筛选
	/// 时每行都会重新取值，几百行就是几百个画刷。这里按需建一次并冻结：
	/// 冻结的画刷不参与变更通知，WPF 可以跨行共享，也不怕多线程读。
	/// </summary>
	public SolidColorBrush ColorBrush
	{
		get
		{
			if (_colorBrush == null)
			{
				var b = new SolidColorBrush(
					System.Windows.Media.Color.FromRgb(_acadColor.R, _acadColor.G, _acadColor.B));
				b.Freeze();
				_colorBrush = b;
			}
			return _colorBrush;
		}
	}

	public bool IsLocked { get; set; }

	public bool IsFrozen { get; set; }

	public bool IsOff { get; set; }

	public string Prefix { get; set; } = "";

	public bool IsHighlighted
	{
		get => _isHighlighted;
		set
		{
			_isHighlighted = value;
			OnPropertyChanged("IsHighlighted");
		}
	}

	public bool IsRecent
	{
		get => _isRecent;
		set
		{
			_isRecent = value;
			OnPropertyChanged("IsRecent");
			OnPropertyChanged("IsNormal");
		}
	}

	public bool IsNormal => !IsRecent;

	/// <summary>
	/// 行尾状态列（§5.5：文案上限 3 字、不换行、定宽 72 右对齐）。
	/// 这里只报"图层自身的事实"，不用红：锁定/冻结/关闭都是可改回的状态，不是错误。
	/// </summary>
	public string StatusText
	{
		get
		{
			if (IsOff) return "已关闭";
			if (IsFrozen) return "已冻结";
			if (IsLocked) return "已锁定";
			return "";
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	public int CompareTo(LayerInfo other)
	{
		if (other == null) return 1;
		return string.Compare(Name, other.Name, StringComparison.OrdinalIgnoreCase);
	}

	public override string ToString()
	{
		return Name;
	}
}
