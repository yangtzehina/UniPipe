using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UniCli.Server.Editor.Handlers
{
    /// <summary>
    /// Why a type or method was left alone. Reported rather than swallowed: a hot reload that
    /// quietly skips half the file is worse than one that refuses, because the developer goes on
    /// believing the edit took.
    /// </summary>
    public enum SkipReason
    {
        None,
        TypeNotLoaded,
        LayoutChanged,
        MethodNotFound,
        SignatureChanged,
        Unsupported,
    }

    public readonly struct SwapCandidate
    {
        public readonly MethodBase Loaded;
        public readonly MethodInfo Compiled;
        public readonly string Description;

        public SwapCandidate(MethodBase loaded, MethodInfo compiled, string description)
        {
            Loaded = loaded;
            Compiled = compiled;
            Description = description;
        }
    }

    public readonly struct SwapSkip
    {
        public readonly string What;
        public readonly SkipReason Reason;
        public readonly string Detail;

        public SwapSkip(string what, SkipReason reason, string detail)
        {
            What = what;
            Reason = reason;
            Detail = detail;
        }
    }

    /// <summary>
    /// Works out which loaded methods an edited file's recompiled types correspond to.
    ///
    /// Recompiling a file produces a second set of types with the same names. Their method bodies
    /// reach instance state through the *new* type's field tokens, but the objects they will run
    /// against are instances of the *old* type. That is fine only while the two lay their fields
    /// out identically — which is why <see cref="FieldLayoutMatches"/> gates every type before any
    /// of its methods are considered. Get that wrong and a swapped body reads whatever happens to
    /// sit at the offset it expects.
    ///
    /// Kept free of Unity and Harmony so the matching rules can be tested directly.
    /// </summary>
    public static class MethodSwapper
    {
        private const BindingFlags AllFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags AllMethods =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>
        /// True when the recompiled type can safely lend its method bodies to instances of the
        /// loaded one: same fields, same types, same order.
        /// </summary>
        public static bool FieldLayoutMatches(Type loaded, Type compiled, out string reason)
        {
            if (loaded == null || compiled == null)
            {
                reason = "missing type";
                return false;
            }

            var loadedFields = loaded.GetFields(AllFields);
            var compiledFields = compiled.GetFields(AllFields);

            if (loadedFields.Length != compiledFields.Length)
            {
                reason = $"field count changed ({loadedFields.Length} loaded, {compiledFields.Length} compiled) — " +
                         "adding or removing a field shifts the ones after it";
                return false;
            }

            for (var i = 0; i < loadedFields.Length; i++)
            {
                var a = loadedFields[i];
                var b = compiledFields[i];
                if (a.Name != b.Name || a.FieldType.FullName != b.FieldType.FullName)
                {
                    reason = $"field {i} changed ({Describe(a)} loaded, {Describe(b)} compiled)";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        private static string Describe(FieldInfo field) => $"{field.FieldType.Name} {field.Name}";

        /// <summary>
        /// Pairs each method of the recompiled types with the loaded method it replaces.
        /// <paramref name="resolveLoadedType"/> maps a type's full name to the loaded type, if any.
        /// </summary>
        public static void Plan(
            IEnumerable<Type> compiledTypes,
            Func<string, Type> resolveLoadedType,
            List<SwapCandidate> candidates,
            List<SwapSkip> skips)
        {
            foreach (var compiledType in compiledTypes)
            {
                var loadedType = resolveLoadedType(compiledType.FullName);
                if (loadedType == null)
                {
                    skips.Add(new SwapSkip(compiledType.FullName, SkipReason.TypeNotLoaded,
                        "the editor has not loaded a type by this name; new types need a recompile"));
                    continue;
                }

                if (!FieldLayoutMatches(loadedType, compiledType, out var layoutReason))
                {
                    skips.Add(new SwapSkip(compiledType.FullName, SkipReason.LayoutChanged, layoutReason));
                    continue;
                }

                foreach (var compiledMethod in compiledType.GetMethods(AllMethods))
                {
                    if (compiledMethod.IsAbstract || compiledMethod.ContainsGenericParameters)
                    {
                        skips.Add(new SwapSkip($"{compiledType.FullName}.{compiledMethod.Name}",
                            SkipReason.Unsupported, "abstract and generic methods are not swapped"));
                        continue;
                    }

                    var parameterTypes = compiledMethod.GetParameters().Select(p => p.ParameterType).ToArray();
                    var loadedMethod = FindLoaded(loadedType, compiledMethod, parameterTypes);

                    if (loadedMethod == null)
                    {
                        skips.Add(new SwapSkip($"{compiledType.FullName}.{compiledMethod.Name}",
                            SkipReason.MethodNotFound,
                            "no loaded method with this name and parameter list; new and re-signed methods need a recompile"));
                        continue;
                    }

                    if (loadedMethod.ReturnType != compiledMethod.ReturnType)
                    {
                        skips.Add(new SwapSkip($"{compiledType.FullName}.{compiledMethod.Name}",
                            SkipReason.SignatureChanged,
                            $"return type changed ({loadedMethod.ReturnType.Name} to {compiledMethod.ReturnType.Name})"));
                        continue;
                    }

                    candidates.Add(new SwapCandidate(loadedMethod, compiledMethod,
                        $"{compiledType.FullName}.{compiledMethod.Name}"));
                }
            }
        }

        private static MethodInfo FindLoaded(Type loadedType, MethodInfo compiled, Type[] parameterTypes)
        {
            // Match on the compiled parameter types by name: the compiled assembly has its own
            // Type objects for types it declares, so reference equality would miss.
            foreach (var candidate in loadedType.GetMethods(AllMethods))
            {
                if (candidate.Name != compiled.Name) continue;
                if (candidate.IsStatic != compiled.IsStatic) continue;

                var candidateParameters = candidate.GetParameters();
                if (candidateParameters.Length != parameterTypes.Length) continue;

                var matches = true;
                for (var i = 0; i < parameterTypes.Length; i++)
                {
                    if (candidateParameters[i].ParameterType.FullName != parameterTypes[i].FullName)
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return candidate;
            }

            return null;
        }
    }
}
