using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PiAgent.PiAi;

namespace PiAgent.Tools
{
    /// <summary>
    /// Registry that converts C# delegates into ToolDefinitions via reflection.
    /// Automatically generates JSON Schema from parameter types.
    /// Supports ToolMetadata, enum params, default values, strict mode, and manual ToolParam API.
    /// </summary>
    public class ToolRegistry
    {
        private readonly List<AgentTool> _tools = new();

        public IReadOnlyList<AgentTool> Tools => _tools.AsReadOnly();

        /// <summary>
        /// Register a tool with no parameters.
        /// </summary>
        public AgentTool Define(string name, string description, Func<string> handler)
        {
            var def = ToolDefinition.NoParams(name, description);
            var tool = new AgentTool(def, (args, ct) => Task.FromResult(handler()));
            _tools.Add(tool);
            return tool;
        }

        /// <summary>
        /// Register an async tool with no parameters.
        /// </summary>
        public AgentTool Define(string name, string description, Func<Task<string>> handler)
        {
            var def = ToolDefinition.NoParams(name, description);
            var tool = new AgentTool(def, async (args, ct) => await handler());
            _tools.Add(tool);
            return tool;
        }

        /// <summary>
        /// Register an async tool with no parameters (with cancellation).
        /// </summary>
        public AgentTool Define(string name, string description, Func<CancellationToken, Task<string>> handler)
        {
            var def = ToolDefinition.NoParams(name, description);
            var tool = new AgentTool(def, async (args, ct) => await handler(ct));
            _tools.Add(tool);
            return tool;
        }

        /// <summary>
        /// Register a tool with a single parameter. Type inferred from delegate.
        /// </summary>
        public AgentTool Define<TParam>(string name, string description, Func<TParam, Task<string>> handler)
        {
            var schema = BuildSchema<TParam>();
            var def = new ToolDefinition(name, description, schema);
            var tool = new AgentTool(def, async (args, ct) =>
            {
                var param = ExtractParam<TParam>(args);
                return await handler(param!);
            });
            _tools.Add(tool);
            return tool;
        }

        /// <summary>
        /// Register a tool with a single parameter (sync).
        /// </summary>
        public AgentTool Define<TParam>(string name, string description, Func<TParam, string> handler)
        {
            var schema = BuildSchema<TParam>();
            var def = new ToolDefinition(name, description, schema);
            var tool = new AgentTool(def, (args, ct) =>
            {
                var param = ExtractParam<TParam>(args);
                return Task.FromResult(handler(param!));
            });
            _tools.Add(tool);
            return tool;
        }

        /// <summary>
        /// Register a tool with a single parameter + CancellationToken.
        /// </summary>
        public AgentTool Define<TParam>(string name, string description, Func<TParam, CancellationToken, Task<string>> handler)
        {
            var schema = BuildSchema<TParam>();
            var def = new ToolDefinition(name, description, schema);
            var tool = new AgentTool(def, async (args, ct) =>
            {
                var param = ExtractParam<TParam>(args);
                return await handler(param!, ct);
            });
            _tools.Add(tool);
            return tool;
        }

        /// <summary>
        /// Register a tool using manual ToolParam definitions (no reflection).
        /// </summary>
        public AgentTool Define(string name, string description, List<ToolParam> parameters,
            Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler,
            bool strict = false)
        {
            var schema = BuildSchemaFromParams(parameters, strict);
            var def = new ToolDefinition(name, description, schema);
            var tool = new AgentTool(def, handler);
            _tools.Add(tool);
            return tool;
        }

        /// <summary>
        /// Register a tool using manual ToolParam definitions (sync handler).
        /// </summary>
        public AgentTool Define(string name, string description, List<ToolParam> parameters,
            Func<Dictionary<string, object?>, string> handler,
            bool strict = false)
        {
            return Define(name, description, parameters, (args, ct) => Task.FromResult(handler(args)), strict);
        }

        /// <summary>
        /// Register a tool from an arbitrary MethodInfo (for advanced scenarios).
        /// Optional ToolMetadata for extra/excluded params and description overrides.
        /// </summary>
        public AgentTool DefineFromMethod(string name, string description, MethodInfo method, object? target = null, ToolMetadata? metadata = null)
        {
            var excludeSet = metadata?.ExcludeParams != null
                ? new HashSet<string>(metadata.ExcludeParams)
                : null;

            var parameters = method.GetParameters();
            var props = new Dictionary<string, JsonSchemaProperty>();
            var required = new List<string>();

            foreach (var param in parameters)
            {
                if (param.ParameterType == typeof(CancellationToken)) continue;
                if (excludeSet != null && excludeSet.Contains(param.Name!)) continue;

                var propSchema = TypeToJsonSchema(param.ParameterType, param.Name!);
                if (param.HasDefaultValue)
                    propSchema.DefaultValue = param.DefaultValue;
                props[param.Name!] = propSchema;
                if (!IsNullable(param.ParameterType) && !param.HasDefaultValue)
                    required.Add(param.Name!);
            }

            // Add extra params from metadata
            if (metadata?.ExtraParams != null)
            {
                foreach (var ep in metadata.ExtraParams)
                {
                    props[ep.Name] = ep.ToSchemaProperty();
                    if (ep.Required)
                        required.Add(ep.Name);
                }
            }

            var schema = new JsonSchema(props, required);
            var def = new ToolDefinition(
                name,
                metadata?.DescriptionOverride ?? description,
                schema);

            var tool = new AgentTool(def, async (args, ct) =>
            {
                var invokeArgs = new List<object?>();
                foreach (var param in parameters)
                {
                    if (param.ParameterType == typeof(CancellationToken))
                    {
                        invokeArgs.Add(ct);
                    }
                    else if (excludeSet != null && excludeSet.Contains(param.Name!))
                    {
                        continue;
                    }
                    else if (args.TryGetValue(param.Name!, out var value))
                    {
                        invokeArgs.Add(ConvertValue(value, param.ParameterType));
                    }
                    else
                    {
                        invokeArgs.Add(param.HasDefaultValue ? param.DefaultValue : null);
                    }
                }

                var result = method.Invoke(target, invokeArgs.ToArray());
                return result switch
                {
                    Task<string> ts => await ts,
                    Task<object> to => (await to)?.ToString() ?? "",
                    string s => s,
                    var v => v?.ToString() ?? ""
                };
            });

            _tools.Add(tool);
            return tool;
        }

        /// <summary>
        /// Find a tool by name.
        /// </summary>
        public AgentTool? Find(string name) => _tools.FirstOrDefault(t => t.Definition.Name == name);

        /// <summary>
        /// Get all tool definitions (for sending to LLM).
        /// </summary>
        public List<AgentTool> GetAll() => _tools.ToList();

        /// <summary>
        /// Build JSON Schema from manual ToolParam list.
        /// </summary>
        private static JsonSchema BuildSchemaFromParams(List<ToolParam> parameters, bool strict)
        {
            var props = new Dictionary<string, JsonSchemaProperty>();
            var required = new List<string>();

            foreach (var p in parameters)
            {
                props[p.Name] = p.ToSchemaProperty();
                if (p.Required)
                    required.Add(p.Name);
            }

            var schema = new JsonSchema(props, required);
            if (strict)
                schema.AdditionalProperties = false;
            return schema;
        }

        /// <summary>
        /// Build JSON Schema from a type.
        /// </summary>
        private static JsonSchema BuildSchema<T>()
        {
            var type = typeof(T);
            if (type == typeof(string))
                return new JsonSchema(new Dictionary<string, JsonSchemaProperty>
                {
                    ["value"] = new JsonSchemaProperty("string")
                }, new List<string> { "value" });

            if (type.IsPrimitive || type == typeof(decimal) || type == typeof(double) || type == typeof(float))
            {
                var jsonType = type == typeof(bool) ? "boolean" : "number";
                return new JsonSchema(new Dictionary<string, JsonSchemaProperty>
                {
                    ["value"] = new JsonSchemaProperty(jsonType)
                }, new List<string> { "value" });
            }

            // Complex type: map properties
            if (type.IsClass || IsNullableStruct(type))
            {
                var props = new Dictionary<string, JsonSchemaProperty>();
                var required = new List<string>();

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    props[prop.Name] = TypeToJsonSchema(prop.PropertyType, prop.Name);
                    required.Add(prop.Name);
                }

                return new JsonSchema(props, required);
            }

            // Fallback: single "value" parameter as string
            return new JsonSchema(new Dictionary<string, JsonSchemaProperty>
            {
                ["value"] = new JsonSchemaProperty("string")
            }, new List<string> { "value" });
        }

        private static JsonSchemaProperty TypeToJsonSchema(Type type, string name)
        {
            var jsonType = type switch
            {
                _ when type == typeof(string) => "string",
                _ when type == typeof(int) || type == typeof(long) || type == typeof(short) => "integer",
                _ when type == typeof(float) || type == typeof(double) || type == typeof(decimal) => "number",
                _ when type == typeof(bool) => "boolean",
                _ when type.IsEnum => "string",
                _ when type == typeof(int[]) || type == typeof(string[]) => "array",
                _ => "string"
            };

            var prop = new JsonSchemaProperty(jsonType, name);

            if (type.IsEnum)
            {
                prop.EnumValues = Enum.GetNames(type).ToList();
            }

            return prop;
        }

        private static T? ExtractParam<T>(Dictionary<string, object?> args)
        {
            // Try "value" key first (for simple single-param tools)
            if (args.TryGetValue("value", out var v))
                return ConvertValue(v, typeof(T)) is T t ? t : default;

            // Try deserializing the whole args dict into T
            var json = JsonSerializer.Serialize(args);
            return JsonSerializer.Deserialize<T>(json);
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null) return null;
            if (value.GetType() == targetType) return value;

            return value switch
            {
                JsonElement el => ConvertJsonElement(el, targetType),
                string s when targetType == typeof(int) => int.TryParse(s, out var i) ? i : default,
                string s when targetType == typeof(double) => double.TryParse(s, out var d) ? d : default,
                string s when targetType == typeof(bool) => bool.TryParse(s, out var b) ? b : default,
                IConvertible c => Convert.ChangeType(c, targetType),
                _ => null
            };
        }

        private static object? ConvertJsonElement(JsonElement el, Type targetType)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => targetType == typeof(int) ? el.GetInt32() :
                                        targetType == typeof(long) ? el.GetInt64() :
                                        targetType == typeof(double) ? el.GetDouble() :
                                        targetType == typeof(bool) ? el.GetBoolean() :
                                        el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Object => JsonSerializer.Deserialize(el.GetRawText(), targetType),
                JsonValueKind.Array => JsonSerializer.Deserialize(el.GetRawText(), targetType),
                _ => el.ToString()
            };
        }

        private static bool IsNullable(Type type)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        }

        private static bool IsNullableStruct(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
        }
    }
}
