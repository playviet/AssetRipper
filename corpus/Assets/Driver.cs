using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// Sits on the one GameObject in the one scene. Its job is to be a root the managed linker cannot argue
// with, and to name the library members the corpus's own methods rely on being present.
//
// Why the library members matter: an il2cpp build runs the managed linker over mscorlib and the engine
// assemblies, so any member nothing calls is deleted. Recovery then cannot name an intrinsic back to a
// method that no longer exists, and the corpus reports a defect that is really a stripped library - that
// exact mistake cost a wrong diagnosis of the struct-in-registers family (`Distance`/`Mathf.Sqrt`).
public class Driver : MonoBehaviour
{
	void Start()
	{
		Debug.Log("Corpus.AddTwo = " + Corpus.AddTwo(2, 3));
		Debug.Log("keepalive = " + Keepalive.Touch());
	}
}

public static class Keepalive
{
	// Not judged by autodiff (it is not `Corpus`), and deliberately so: this only has to be reachable.
	public static string Touch()
	{
		float f = Mathf.Sqrt(2f) + MathF.Sqrt(3f) + Mathf.Abs(-1f) + Mathf.Floor(1.5f) + Mathf.Ceil(1.5f);
		f += Mathf.Sin(1f) + Mathf.Cos(1f) + Mathf.Round(1.5f) + Mathf.Sign(-2f);
		f += Mathf.Min(1f, 2f) + Mathf.Max(1f, 2f) + Mathf.Clamp01(0.5f) + Mathf.Lerp(0f, 1f, 0.5f);
		f += Mathf.PI + Mathf.Pow(2f, 3f) + Mathf.Log(2f) + Mathf.Exp(1f) + Mathf.Atan2(1f, 1f);
		double d = Math.Sqrt(2.0) + Math.Truncate(1.5) + Math.Abs(-1.0) + Math.Floor(1.5) + Math.Ceiling(1.5);
		d += Math.Round(1.5) + Math.Pow(2.0, 3.0) + Math.Log(2.0) + Math.Exp(1.0) + Math.Sign(-2.0);
		d += Math.Min(1.0, 2.0) + Math.Max(1.0, 2.0) + Math.Sin(1.0) + Math.Cos(1.0) + Math.Atan2(1.0, 1.0);
		long l = Math.Abs(-3L) + Math.Min(1L, 2L) + Math.Max(1L, 2L) + Math.BigMul(2, 3);
		int i = Math.Abs(-3) + Math.Min(1, 2) + Math.Max(1, 2) + Math.DivRem(7, 2, out int rem) + rem;

		StringBuilder builder = new StringBuilder();
		builder.Append("a").Append(1).Append(1f).Append(true).Append('c').AppendLine();
		builder.Insert(0, "z").Remove(0, 1).Replace("a", "b");

		string s = string.Format("{0}{1}", 1, 2) + string.Join(",", new[] { "a", "b" })
			+ string.Concat("a", "b") + "x".PadLeft(2) + "x".PadRight(2) + " x ".Trim()
			+ "abc".Substring(1) + "abc".Replace("a", "b") + "abc".ToUpper() + "abc".ToLower()
			+ "abc".IndexOf('b') + "abc".Contains("b") + "abc".StartsWith("a") + "abc".EndsWith("c")
			+ string.IsNullOrEmpty("") + string.IsNullOrWhiteSpace(" ") + "a,b".Split(',').Length
			+ 1.ToString(CultureInfoInvariant()) + 1f.ToString("0.##") + 1.0.ToString("0.##")
			+ int.Parse("1") + float.Parse("1") + long.Parse("1")
			+ int.TryParse("1", out int _) + float.TryParse("1", out float _)
			+ "abc".CompareTo("abd") + string.Equals("a", "a") + "a".GetHashCode()
			+ char.IsDigit('1') + char.IsLetter('a') + char.ToUpper('a') + char.ToLower('A')
			+ Convert.ToInt32("1") + Convert.ToString(1) + Convert.ToSingle("1") + Convert.ToDouble("1");

		List<int> list = new List<int> { 1, 2, 3 };
		list.Add(4); list.Insert(0, 0); list.Remove(1); list.RemoveAt(0); list.Contains(2);
		list.IndexOf(2); list.Sort(); list.Reverse(); list.Clear(); list.AddRange(new[] { 1, 2 });
		int[] array = list.ToArray();
		Array.Sort(array); Array.Reverse(array); Array.IndexOf(array, 1); Array.Resize(ref array, 3);
		Array.Copy(array, array, 1); Array.Clear(array, 0, 1);

		Dictionary<string, int> map = new Dictionary<string, int> { { "a", 1 } };
		map.TryGetValue("a", out int found); map.ContainsKey("a"); map.Remove("a"); map["b"] = 2;

		Vector2 v2 = new Vector2(1f, 2f) + Vector2.one * 2f;
		Vector3 v3 = new Vector3(1f, 2f, 3f) + Vector3.one;
		Color color = new Color(1f, 1f, 1f, 1f) * 0.5f;
		Quaternion quaternion = Quaternion.Euler(0f, 90f, 0f);
		float dot = Vector3.Dot(v3, Vector3.up) + Vector2.Distance(v2, Vector2.zero)
			+ Vector3.Distance(v3, Vector3.zero) + v3.magnitude + v2.magnitude + v3.sqrMagnitude;

		return s + f + d + l + i + found + array.Length + map.Count + builder.Length
			+ dot + color.r + quaternion.w + CultureInfoInvariant();
	}

	static IFormatProvider CultureInfoInvariant()
	{
		return CultureInfo.InvariantCulture;
	}
}
