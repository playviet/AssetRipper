namespace AssetRipper.Export.Configuration;

public enum ScriptIlExportMode
{
	/// <summary>
	/// Nothing extra is written.
	/// </summary>
	None,
	/// <summary>
	/// Beside every decompiled script, write the CIL that the script was decompiled from.
	/// </summary>
	/// <remarks>
	/// Intended for reading a recovered Il2Cpp project, where the C# is incomplete and the IL still carries the exact
	/// types, the real call targets, and the address to go back to in the binary.
	/// </remarks>
	Companion,
}
