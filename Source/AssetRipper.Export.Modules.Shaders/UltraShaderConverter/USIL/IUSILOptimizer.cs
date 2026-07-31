using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.Function;
using AssetRipper.Export.Modules.Shaders.Processor;

namespace AssetRipper.Export.Modules.Shaders.UltraShaderConverter.USIL
{
    public interface IUSILOptimizer
    {
        public bool Run(UShaderProgram shader, ShaderParams shaderData);
    }
}
