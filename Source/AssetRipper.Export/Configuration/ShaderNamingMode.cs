namespace AssetRipper.Export.Configuration;

/// <summary>
/// What to call exported shaders.
/// </summary>
public enum ShaderNamingMode
{
	/// <summary>
	/// Keep the name the shader had in the game.
	/// </summary>
	Original,

	/// <summary>
	/// Append a suffix so the shader cannot collide with one Unity already provides.
	/// </summary>
	/// <remarks>
	/// A build contains the shaders of whatever render pipeline it used, and the exported project usually ends up
	/// referencing that same pipeline as a package. Unity then sees two shaders claiming the same name and silently
	/// picks one, so materials can bind to the package's shader instead of the exported one. Renaming makes the
	/// exported shaders distinct, at the cost of no longer matching the names the game used.
	/// </remarks>
	Suffixed,
}
