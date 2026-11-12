using PascalABCNet.LanguageServer.PluginApi;

namespace FullLineCompletionLspPlugin
{
    public sealed class FullLineCompletionPlugin : ILanguageServerPlugin
    {
        public string Name => "PascalABC.NET Full Line Completion";

        public void Initialize(ILanguageServerPluginContext context)
        {
            var serverManager = LlamaServerManager.StartNew();
            context.RegisterInlineCompletionProvider(new FullLineCompletionProvider(serverManager));
        }
    }
}
