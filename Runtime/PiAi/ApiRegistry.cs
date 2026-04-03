using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PiAgent.PiAi
{
    /// <summary>
    /// Function signature for streaming LLM completions.
    /// </summary>
    public delegate Task<AssistantMessage> StreamFunction(
        ModelConfig model, AgentContext context, CancellationToken ct = default);

    /// <summary>
    /// An API provider that can handle streaming completions for a specific API type.
    /// </summary>
    public interface IApiProvider
    {
        /// <summary>The API type this provider handles (e.g., "openai-completions").</summary>
        string Api { get; }

        /// <summary>Stream a completion response.</summary>
        Task<AssistantMessage> Stream(ModelConfig model, AgentContext context, CancellationToken ct = default);
    }

    /// <summary>
    /// Registry for LLM API providers. Matches pi-ai's api-registry.ts pattern.
    /// </summary>
    public class ApiRegistry
    {
        private static readonly Dictionary<string, IApiProvider> _providers = new();
        private static readonly Dictionary<string, ModelConfig> _models = new();

        /// <summary>
        /// Register an API provider for a specific API type.
        /// </summary>
        public static void RegisterProvider(IApiProvider provider)
        {
            _providers[provider.Api] = provider;
        }

        /// <summary>
        /// Unregister all providers registered with a given source ID.
        /// </summary>
        public static void UnregisterProvider(string api)
        {
            _providers.Remove(api);
        }

        /// <summary>
        /// Get a registered provider by API type.
        /// </summary>
        public static IApiProvider? GetProvider(string api)
        {
            return _providers.TryGetValue(api, out var p) ? p : null;
        }

        /// <summary>
        /// Get all registered providers.
        /// </summary>
        public static IReadOnlyList<IApiProvider> GetAllProviders()
        {
            var list = new List<IApiProvider>(_providers.Values);
            return list.AsReadOnly();
        }

        /// <summary>
        /// Clear all registered providers.
        /// </summary>
        public static void ClearProviders()
        {
            _providers.Clear();
        }

        /// <summary>
        /// Register a model by its ID.
        /// </summary>
        public static void RegisterModel(ModelConfig model)
        {
            _models[model.Id] = model;
        }

        /// <summary>
        /// Get a registered model by ID.
        /// </summary>
        public static ModelConfig? GetModel(string id)
        {
            return _models.TryGetValue(id, out var m) ? m : null;
        }

        /// <summary>
        /// Get all registered models.
        /// </summary>
        public static IReadOnlyList<ModelConfig> GetAllModels()
        {
            var list = new List<ModelConfig>(_models.Values);
            return list.AsReadOnly();
        }

        /// <summary>
        /// Clear all registered models.
        /// </summary>
        public static void ClearModels()
        {
            _models.Clear();
        }

        /// <summary>
        /// Clear everything (providers and models).
        /// </summary>
        public static void Clear()
        {
            _providers.Clear();
            _models.Clear();
        }
    }
}
