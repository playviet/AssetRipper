using AssetRipper.Export.Configuration;
using System.Text.Json.Serialization;

namespace AssetRipper.Export.UnityProjects.Configuration;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AssetPathOverrideData))]
[JsonSerializable(typeof(UserPackageData))]
[JsonSerializable(typeof(EmbeddedFileData))]
internal sealed partial class UserDataContext : JsonSerializerContext
{
}
