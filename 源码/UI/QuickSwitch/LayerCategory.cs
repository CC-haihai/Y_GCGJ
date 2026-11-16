namespace AutoLayer.UI.QuickSwitch;

public class LayerCategory
{
	public string Name { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public int Count { get; set; }

	public bool IsOther => Name == "__other__";
}
