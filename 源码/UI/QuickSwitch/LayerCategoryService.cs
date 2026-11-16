using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoLayer.UI.QuickSwitch;

public static class LayerCategoryService
{
	public static List<LayerCategory> DetectCategories(IEnumerable<string> layerNames)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		foreach (string layerName in layerNames)
		{
			if (string.IsNullOrEmpty(layerName) || layerName == "0") continue;
			string text = ExtractPrefix(layerName);
			if (!string.IsNullOrEmpty(text))
			{
				string key = text.ToUpperInvariant();
				if (dictionary.ContainsKey(key)) dictionary[key]++;
				else dictionary[key] = 1;
			}
		}
		List<LayerCategory> list = (from g in (from g in dictionary
				where g.Value >= 2
				orderby g.Key ascending
				select g)
			select new LayerCategory
			{
				Name = g.Key,
				DisplayName = g.Key,
				Count = g.Value
			}).ToList();
		HashSet<string> categorizedPrefixes = new HashSet<string>(list.Select((LayerCategory c) => c.Name));
		int num = dictionary.Where((KeyValuePair<string, int> g) => !categorizedPrefixes.Contains(g.Key)).Sum((KeyValuePair<string, int> g) => g.Value);
		if (layerNames.Any((string n) => n == "0")) num++;
		list.Add(new LayerCategory
		{
			Name = "__other__",
			DisplayName = "其他",
			Count = num
		});
		return list;
	}

	/// <summary>
	/// 取图层名的「分类前缀」：第一个 - _ . 之前那一段，截到 8 字，超了退化成前 3 字。
	///
	/// 这个函数在搜索框每敲一个字符、每个图层都要跑一遍（几百层 × 每键），
	/// 原来用 Split 会固定分配一个 char[] + 一个 string[]，改 IndexOfAny 后
	/// 没有分隔符的图层（绝大多数）零分配。行为逐字保持不变。
	/// </summary>
	static readonly char[] Separators = { '-', '_', '.' };

	public static string ExtractPrefix(string layerName)
	{
		if (string.IsNullOrEmpty(layerName)) return "";
		int cut = layerName.IndexOfAny(Separators);
		string text = (cut < 0 ? layerName : layerName.Substring(0, cut)).Trim();
		if (text.Length >= 1 && text.Length <= 8) return text.ToUpperInvariant();
		if (layerName.Length < 3) return layerName.ToUpperInvariant();
		return layerName.Substring(0, 3).ToUpperInvariant();
	}
}
