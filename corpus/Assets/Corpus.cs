// The ground-truth corpus. Every method here has known source, so a recovered copy can be executed against
// this one - see scratchpad-tools/autodiff.py. It is the only instrument in this project that asks whether a
// recovered body computes the right answer rather than whether it compiles whole.
//
// Rules autodiff.py imposes, each of which breaks it silently if broken:
//   * the subject is `public static class Corpus` at column 0. autodiff lifts the WHOLE class into two
//     namespaces, so anything a method calls must be inside the class or a top-level type in this file.
//   * a judged method is `public static <ret> <Name>(` on ONE line, returns something (never `void`), and
//     every parameter type must be a key in autodiff.py's GENERATORS table. `out`/`ref`/`params` parameters
//     are not judged - wrap them in a method that returns the answer instead.
//   * supporting types are taken from THIS file for BOTH sides, so a defect in a struct's own layout is not
//     what this measures; method bodies are. They need a ToString() override or every value of them
//     describes as its type name and every comparison passes.
//   * NEVER put a shape in a static field initialiser. One throw there takes the class initialiser down and
//     every method fails at once, which destroys the corpus as an instrument.
//   * no UnityEngine types: the harness is a plain console app. Use System.Math / System.MathF.
//
// Adding a shape is adding a method. Rebuild with BuildCorpus.Build, re-export, re-run autodiff.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public enum Colour
{
	None = 0,
	Red = 1,
	Green = 2,
	Blue = 3,
	Cyan = 4,
	Magenta = 5,
	Yellow = 6,
	White = 7,
}

// Two floats: what a Vector2 is to the ABI - one HFA passed in v0/v1 and returned in them too.
public struct Pair
{
	public float X;
	public float Y;

	public Pair(float x, float y)
	{
		X = x;
		Y = y;
	}

	public override string ToString()
	{
		return "(" + X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture) + ")";
	}
}

// Three floats: a Vector3.
public struct Triple
{
	public float X;
	public float Y;
	public float Z;

	public Triple(float x, float y, float z)
	{
		X = x;
		Y = y;
		Z = z;
	}

	public override string ToString()
	{
		return "(" + X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture)
			+ "," + Z.ToString(CultureInfo.InvariantCulture) + ")";
	}
}

// Four floats: a Color. Still an HFA, so still the vector registers, but it fills all four.
public struct Quad
{
	public float R;
	public float G;
	public float B;
	public float A;

	public Quad(float r, float g, float b, float a)
	{
		R = r;
		G = g;
		B = b;
		A = a;
	}

	public override string ToString()
	{
		return "(" + R.ToString(CultureInfo.InvariantCulture) + "," + G.ToString(CultureInfo.InvariantCulture)
			+ "," + B.ToString(CultureInfo.InvariantCulture) + "," + A.ToString(CultureInfo.InvariantCulture) + ")";
	}
}

// A struct with a struct inside it, and a mixed layout - not an HFA, so it goes on the stack or through x8.
public struct Nested
{
	public Pair A;
	public int Count;
	public Pair B;

	public override string ToString()
	{
		return A + "/" + Count + "/" + B;
	}
}

// Bigger than 16 bytes: returned through the buffer the caller passes in x8.
public struct Wide
{
	public long One;
	public long Two;
	public long Three;
	public int Four;

	public override string ToString()
	{
		return One + ":" + Two + ":" + Three + ":" + Four;
	}
}

public interface IShape
{
	int Sides();
	string Name();
}

public class Square : IShape
{
	public int Sides()
	{
		return 4;
	}

	public string Name()
	{
		return "square";
	}
}

public class Triangle : IShape
{
	public int Sides()
	{
		return 3;
	}

	public string Name()
	{
		return "triangle";
	}
}

// An abstract base with a virtual call and a constructor that il2cpp will inline into its caller.
public abstract class Shape
{
	public abstract int Area();
}

public class Box : Shape
{
	private int _side;

	public Box(int side)
	{
		_side = side;
	}

	public override int Area()
	{
		return _side * _side;
	}
}

public class Circle : Shape
{
	private int _radius;

	public Circle(int radius)
	{
		_radius = radius;
	}

	public override int Area()
	{
		return 3 * _radius * _radius;
	}
}

public class Counter : IDisposable
{
	public int Value;

	public void Dispose()
	{
		Value = -1;
	}
}

public static class Corpus
{
	// ---- arithmetic and branching ---------------------------------------------------------------

	public static int AddTwo(int a, int b)
	{
		return a + b;
	}

	public static int Clamp(int value, int low, int high)
	{
		if (value < low)
		{
			return low;
		}
		if (value > high)
		{
			return high;
		}
		return value;
	}

	// `&&` on two comparisons is one CCMP on arm64, not two branches.
	public static int Both(int a, int b)
	{
		if (a > 0 && b > 0)
		{
			return a + b;
		}
		return -1;
	}

	public static int Either(int a, int b)
	{
		if (a > 100 || b < -20)
		{
			return 1;
		}
		return 0;
	}

	// The ternary that chooses between two constants, and the one that chooses an offset.
	public static int Ternary(int a, bool flag)
	{
		int step = flag ? 4 : 12;
		return a * step + (a > 0 ? 1 : 2);
	}

	// A 64-bit shift whose result must not be truncated to 32.
	public static long Mix(int a, int b)
	{
		return ((long)a << 16) ^ (long)b;
	}

	// A 32-bit operation on a value that is 64 bits wide: recovery has to keep the width.
	public static long Bits(long value, int shift)
	{
		int low = (int)value;
		return (long)(low << (shift & 15)) + (value >> 3);
	}

	// Logical right shift against arithmetic right shift - the same mnemonic family, different answers.
	public static long Shifts(int a, int b)
	{
		int amount = b & 31;
		long logical = (uint)a >> amount;
		long arithmetic = a >> amount;
		return logical * 3 + arithmetic;
	}

	public static int Overflow(int a, int b)
	{
		checked
		{
			return a * 1000 + b * 1000;
		}
	}

	// Narrowing conversions: each one is a different extend folded into the operand.
	public static int Narrow(int value)
	{
		byte b = (byte)value;
		sbyte s = (sbyte)value;
		short h = (short)value;
		ushort u = (ushort)value;
		return b + s + h + u;
	}

	// Magic division: a signed constant divide is a multiply and two shifts, and the sign correction is the
	// part that goes missing.
	public static int DivMagic(int value)
	{
		return value / 7 + value / 10 + value / 100;
	}

	public static int Modulo(int a, int b)
	{
		if (b == 0)
		{
			return -1;
		}
		return a % b;
	}

	// A jump table: sub / cmp / b.hi / adrp+add / ldr [table, index, sxtw #2].
	public static int Weight(int kind)
	{
		switch (kind)
		{
			case 0: return 10;
			case 1: return 25;
			case 2: return 40;
			case 3: return 55;
			case 4: return 70;
			case 5: return 85;
			case 6: return 100;
			default: return 0;
		}
	}

	// A switch on a string is a hash and a chain of compares.
	public static int Kind(string word)
	{
		switch (word)
		{
			case null: return -1;
			case "": return 0;
			case "one": return 1;
			case "two": return 2;
			case "three": return 3;
			case "four": return 4;
			default: return 99;
		}
	}

	public static int WrapLevel(int level)
	{
		if (level < 30)
		{
			return level;
		}
		return (level - 30) % 20 + 10;
	}

	public static float EaseInOut(float t)
	{
		if (t < 0.5f)
		{
			return 2f * t * t;
		}
		return -1f + (4f - 2f * t) * t;
	}

	// ---- structs in the vector registers ---------------------------------------------------------

	public static float Distance(Pair a, Pair b)
	{
		float dx = a.X - b.X;
		float dy = a.Y - b.Y;
		return (float)Math.Sqrt(dx * dx + dy * dy);
	}

	public static float Length3(Triple t)
	{
		return (float)Math.Sqrt(t.X * t.X + t.Y * t.Y + t.Z * t.Z);
	}

	public static float Luminance(Quad c)
	{
		return c.R * 0.299f + c.G * 0.587f + c.B * 0.114f + c.A * 0f;
	}

	// A two-float struct returned: it comes back in v0 and v1, and the callee has to be able to name it.
	public static Pair Scale(Pair p, float factor)
	{
		return new Pair(p.X * factor, p.Y * factor);
	}

	public static Triple Cross(Triple a, Triple b)
	{
		return new Triple(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
	}

	public static Quad Blend(Quad a, Quad b)
	{
		return new Quad((a.R + b.R) * 0.5f, (a.G + b.G) * 0.5f, (a.B + b.B) * 0.5f, (a.A + b.A) * 0.5f);
	}

	// Bigger than 16 bytes: the caller passes a buffer in x8 and the callee writes through it.
	public static Wide Spread(int seed)
	{
		Wide w = default(Wide);
		w.One = seed * 3L;
		w.Two = seed * 5L;
		w.Three = seed * 7L;
		w.Four = seed;
		return w;
	}

	public static float NestedSum(Nested n)
	{
		return n.A.X + n.A.Y + n.B.X + n.B.Y + n.Count;
	}

	// A read at a struct field's offset inside another struct field.
	public static float PairField(Nested n)
	{
		Pair inner = n.B;
		return inner.Y - n.A.X;
	}

	// A struct built up field by field and then handed to a call.
	public static float BuildAndPass(float x, float y)
	{
		Pair p = default(Pair);
		p.X = x;
		p.Y = y;
		return Distance(p, new Pair(0f, 0f));
	}

	// A struct cleared to zero - one wide vector store, not four float writes.
	public static string ClearAndSet(float x)
	{
		Quad q = default(Quad);
		q.A = x;
		return q.ToString();
	}

	// ---- arrays -----------------------------------------------------------------------------------

	public static int CountOf(Colour[] cells, Colour wanted)
	{
		if (cells == null)
		{
			return -1;
		}
		int found = 0;
		for (int i = 0; i < cells.Length; i++)
		{
			if (cells[i] == wanted)
			{
				found++;
			}
		}
		return found;
	}

	public static bool AllNone(Colour[] cells)
	{
		if (cells == null)
		{
			return true;
		}
		foreach (Colour cell in cells)
		{
			if (cell != Colour.None)
			{
				return false;
			}
		}
		return true;
	}

	public static int IndexOfFirst(int[] values, int wanted)
	{
		if (values == null)
		{
			return -1;
		}
		for (int i = 0; i < values.Length; i++)
		{
			if (values[i] == wanted)
			{
				return i;
			}
		}
		return -1;
	}

	public static int SumJagged(int[][] rows)
	{
		int total = 0;
		for (int i = 0; i < rows.Length; i++)
		{
			int[] row = rows[i];
			if (row == null)
			{
				continue;
			}
			for (int j = 0; j < row.Length; j++)
			{
				total += row[j];
			}
		}
		return total;
	}

	public static int Diagonal(int[,] grid)
	{
		int total = 0;
		for (int i = 0; i < 2; i++)
		{
			total += grid[i, i] * (i + 1);
		}
		return total;
	}

	public static int Matching(int[] a, int[] b)
	{
		if (a == null || b == null)
		{
			return -1;
		}
		int count = 0;
		int length = a.Length < b.Length ? a.Length : b.Length;
		for (int i = 0; i < length; i++)
		{
			if (a[i] == b[i])
			{
				count++;
			}
		}
		return count;
	}

	// h * 31 + c over a string: a left shift the lifter has read as a right one before.
	public static int Hash(string text)
	{
		if (text == null)
		{
			return 0;
		}
		int h = 17;
		for (int i = 0; i < text.Length; i++)
		{
			h = h * 31 + text[i];
		}
		return h;
	}

	public static int[] Reversed(int[] values)
	{
		if (values == null)
		{
			return null;
		}
		int[] copy = new int[values.Length];
		for (int i = 0; i < values.Length; i++)
		{
			copy[i] = values[values.Length - 1 - i];
		}
		return copy;
	}

	public static int[] Build(int count)
	{
		int n = count & 7;
		int[] made = new int[n];
		for (int i = 0; i < n; i++)
		{
			made[i] = i * i;
		}
		return made;
	}

	public static int Slice(int[] values, int from)
	{
		if (values == null || values.Length == 0)
		{
			return 0;
		}
		int start = from & 1;
		int[] part = new int[values.Length - start];
		Array.Copy(values, start, part, 0, part.Length);
		int total = 0;
		foreach (int v in part)
		{
			total += v;
		}
		return total;
	}

	// An array of a non-primitive struct: the stride is sizeof(Pair), which had no width for a long time.
	public static float SumPairs(Pair[] points)
	{
		if (points == null)
		{
			return -1f;
		}
		float total = 0f;
		for (int i = 0; i < points.Length; i++)
		{
			total += points[i].X - points[i].Y;
		}
		return total;
	}

	public static float FirstPairX(Pair[] points)
	{
		if (points == null || points.Length == 0)
		{
			return 0f;
		}
		return points[0].X + points[points.Length - 1].Y;
	}

	// Covariant array store, then virtual dispatch through the base, then a constructor il2cpp inlines.
	public static int Areas(int side)
	{
		Shape[] shapes = new Shape[3];
		shapes[0] = new Box(side & 7);
		shapes[1] = new Circle(side & 3);
		shapes[2] = new Box(2);
		int total = 0;
		for (int i = 0; i < shapes.Length; i++)
		{
			total += shapes[i].Area();
		}
		return total;
	}

	// ---- foreach ---------------------------------------------------------------------------------

	public static int Total(List<int> values)
	{
		int total = 0;
		foreach (int value in values)
		{
			total += value;
		}
		return total;
	}

	// Copies first, on purpose: autodiff hands the SAME object to both sides, so a method that mutates its
	// argument reports a difference that is entirely its own doing.
	public static int Grow(List<int> given)
	{
		List<int> values = new List<int>(given);
		values.Add(9);
		values.Insert(0, 1);
		values.RemoveAt(values.Count - 1);
		int total = 0;
		for (int i = 0; i < values.Count; i++)
		{
			total += values[i] * (i + 1);
		}
		return total;
	}

	// Dictionary mutation and a foreach over its pairs.
	public static string Tally(string[] words)
	{
		if (words == null)
		{
			return "null";
		}
		Dictionary<string, int> counts = new Dictionary<string, int>();
		foreach (string word in words)
		{
			string key = word ?? "<null>";
			int already;
			if (counts.TryGetValue(key, out already))
			{
				counts[key] = already + 1;
			}
			else
			{
				counts.Add(key, 1);
			}
		}
		int sum = 0;
		foreach (KeyValuePair<string, int> pair in counts)
		{
			sum += pair.Value * pair.Key.Length;
		}
		return counts.Count + ":" + sum;
	}

	public static int Lookup(Dictionary<string, int> map)
	{
		if (map == null)
		{
			return -1;
		}
		int found;
		if (map.TryGetValue("a", out found))
		{
			return found * 10;
		}
		return -2;
	}

	// foreach over an interface list: an interface call per element.
	public static int TotalSides(IList<IShape> shapes)
	{
		int total = 0;
		foreach (IShape shape in shapes)
		{
			total += shape.Sides();
		}
		return total;
	}

	public static string Names(IList<IShape> shapes)
	{
		StringBuilder builder = new StringBuilder();
		foreach (IShape shape in shapes)
		{
			builder.Append(shape.Name()).Append('|');
		}
		return builder.ToString();
	}

	public static int Enumerated(IEnumerable<int> values)
	{
		int total = 0;
		foreach (int value in values)
		{
			total = total * 2 + value;
		}
		return total;
	}

	// ---- generics: the shared-body seam ----------------------------------------------------------

	// T = a reference type, so il2cpp compiles ONE shared body and passes the type through MethodInfo.
	private static int CountNonNull<T>(T[] items) where T : class
	{
		int found = 0;
		for (int i = 0; i < items.Length; i++)
		{
			if (items[i] != null)
			{
				found++;
			}
		}
		return found;
	}

	private static T Pick<T>(T[] items, int index)
	{
		if (items == null || items.Length == 0)
		{
			return default(T);
		}
		return items[((index % items.Length) + items.Length) % items.Length];
	}

	private static int SumAll<T>(T[] items, Func<T, int> measure)
	{
		int total = 0;
		foreach (T item in items)
		{
			total += measure(item);
		}
		return total;
	}

	public static int SharedCount(string[] words)
	{
		if (words == null)
		{
			return -1;
		}
		return CountNonNull(words);
	}

	public static string SharedPick(string[] words, int index)
	{
		return Pick(words, index) ?? "<none>";
	}

	// T = a value type, so this one is specialised rather than shared.
	public static int ValuePick(int[] values, int index)
	{
		return Pick(values, index);
	}

	public static int SharedMeasure(string[] words)
	{
		if (words == null)
		{
			return -1;
		}
		return SumAll(words, w => w == null ? 0 : w.Length);
	}

	public static int SwapAndSum(int a, int b)
	{
		int[] pair = new int[] { a, b };
		int t = pair[0];
		pair[0] = pair[1];
		pair[1] = t;
		return pair[0] * 2 + pair[1];
	}

	// ---- Nullable --------------------------------------------------------------------------------

	public static int OrElse(int value, bool present)
	{
		int? maybe = present ? value : (int?)null;
		return maybe ?? -1;
	}

	public static string NullableChain(int value, bool present)
	{
		int? maybe = present ? (int?)(value * 2) : null;
		if (maybe.HasValue)
		{
			return "v" + maybe.Value;
		}
		return "none";
	}

	public static int NullableSum(int[] values)
	{
		if (values == null)
		{
			return -1;
		}
		int? total = null;
		foreach (int value in values)
		{
			total = (total ?? 0) + value;
		}
		return total.GetValueOrDefault(-2);
	}

	// ---- boxing and casts ------------------------------------------------------------------------

	public static string Boxed(int value)
	{
		object o = value;
		if (o is int)
		{
			int back = (int)o;
			return "int" + (back + 1);
		}
		return "other";
	}

	public static string BoxedFloat(float value)
	{
		object o = value;
		string s = o.ToString();
		return s + "/" + ((float)o + 1f).ToString(CultureInfo.InvariantCulture);
	}

	public static int CastChain(int value)
	{
		object o = value;
		IConvertible convertible = (IConvertible)o;
		return convertible.ToInt32(CultureInfo.InvariantCulture) + 1;
	}

	public static string AsOrNull(int which)
	{
		object o = which % 2 == 0 ? (object)"text" : (object)7;
		string s = o as string;
		if (s == null)
		{
			return "notstring";
		}
		return s.ToUpperInvariant();
	}

	// ---- strings ---------------------------------------------------------------------------------

	public static string Format(int a, float b)
	{
		return string.Format(CultureInfo.InvariantCulture, "{0}-{1:0.##}|{0}", a, b);
	}

	public static string Join(string[] parts)
	{
		if (parts == null)
		{
			return "null";
		}
		return string.Join(",", parts);
	}

	public static string Builder(string[] parts)
	{
		if (parts == null)
		{
			return "null";
		}
		StringBuilder builder = new StringBuilder();
		for (int i = 0; i < parts.Length; i++)
		{
			builder.Append(i).Append(':').Append(parts[i] ?? "?").Append(';');
		}
		return builder.ToString();
	}

	public static bool TooShort(string text)
	{
		return text == null || text.Length < 3;
	}

	public static int Words(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return 0;
		}
		string[] parts = text.Split(' ');
		int count = 0;
		foreach (string part in parts)
		{
			if (part.Length > 0)
			{
				count++;
			}
		}
		return count;
	}

	public static string Interpolated(int a, string b)
	{
		return $"a={a} b={b ?? "-"} sum={a + (b == null ? 0 : b.Length)}";
	}

	public static string Describe(Colour colour)
	{
		return colour.ToString() + "=" + (int)colour;
	}

	// ---- out parameters --------------------------------------------------------------------------

	private static bool TryParseInt(string text, out int value)
	{
		return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
	}

	private static void MinMax(int[] values, out int low, out int high)
	{
		low = int.MaxValue;
		high = int.MinValue;
		for (int i = 0; i < values.Length; i++)
		{
			if (values[i] < low)
			{
				low = values[i];
			}
			if (values[i] > high)
			{
				high = values[i];
			}
		}
	}

	public static int ParseOrDefault(string text)
	{
		int value;
		if (TryParseInt(text, out value))
		{
			return value;
		}
		return -999;
	}

	public static string Range(int[] values)
	{
		if (values == null || values.Length == 0)
		{
			return "empty";
		}
		int low;
		int high;
		MinMax(values, out low, out high);
		return low + ".." + high;
	}

	// ---- iterators, delegates, exceptions ---------------------------------------------------------

	public static IEnumerable<int> Steps(int count)
	{
		int n = count & 7;
		for (int i = 0; i < n; i++)
		{
			yield return i * i;
		}
	}

	public static int SumSteps(int count)
	{
		int total = 0;
		foreach (int step in Steps(count))
		{
			total += step;
		}
		return total;
	}

	// A coroutine's shape: yields a reference, and yields more than once from more than one place.
	public static IEnumerable<string> Ticks(int count)
	{
		yield return "start";
		int n = count & 3;
		for (int i = 0; i < n; i++)
		{
			yield return null;
			yield return "t" + i;
		}
		yield return "end";
	}

	public static string TickText(int count)
	{
		StringBuilder builder = new StringBuilder();
		foreach (string tick in Ticks(count))
		{
			builder.Append(tick ?? ".");
		}
		return builder.ToString();
	}

	public static int Filtered(int[] values, Predicate<int> keep)
	{
		if (values == null)
		{
			return -1;
		}
		int total = 0;
		foreach (int value in values)
		{
			if (keep(value))
			{
				total += value;
			}
		}
		return total;
	}

	// A lambda that captures a local: a display class, a newobj and a delegate.
	public static int Closure(int[] values, int threshold)
	{
		if (values == null)
		{
			return -1;
		}
		Predicate<int> big = delegate(int v) { return v > threshold; };
		int count = 0;
		foreach (int value in values)
		{
			if (big(value))
			{
				count++;
			}
		}
		return count;
	}

	public static int Divide(int a, int b)
	{
		try
		{
			return a / b;
		}
		catch (DivideByZeroException)
		{
			return -1;
		}
	}

	public static int Guarded(int a, int b)
	{
		int result = 0;
		try
		{
			result = a / b;
		}
		catch (Exception)
		{
			result = -7;
		}
		finally
		{
			result = result * 2;
		}
		return result;
	}

	public static int Thrown(int value)
	{
		try
		{
			if (value < 0)
			{
				throw new ArgumentOutOfRangeException("value");
			}
			return value * 2;
		}
		catch (ArgumentOutOfRangeException)
		{
			return -5;
		}
	}

	public static int Using(int value)
	{
		using (Counter counter = new Counter())
		{
			counter.Value = value * 3;
			return counter.Value + 1;
		}
	}

	// ---- static state ------------------------------------------------------------------------------

	private static event Func<int, int> Adjust;

	// Every event accessor is a compare-exchange loop. Subscribing and unsubscribing inside the one call
	// keeps the corpus deterministic across iterations.
	public static int EventRoundTrip(int value)
	{
		Func<int, int> handler = v => v + 5;
		Adjust += handler;
		int answer = Adjust(value);
		Adjust -= handler;
		return answer + (Adjust == null ? 1000 : 0);
	}
}
