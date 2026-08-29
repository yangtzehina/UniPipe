using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UniCli.Server.Editor.Handlers;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// Hot reload works by compiling an edited file into a second set of types with the same names
    /// and lending their method bodies to the objects already alive. Those bodies reach instance
    /// state through the *new* type's field tokens while `this` is an instance of the *old* one —
    /// which is only safe while both lay their fields out identically. These cover the rules that
    /// decide whether a swap is allowed, because getting them wrong reads the wrong memory rather
    /// than failing loudly.
    /// </summary>
    [TestFixture]
    public class MethodSwapperTests
    {
        // "Loaded" shapes.
        private class Loaded
        {
            private int _a = 1;
            private string _b = "b";
            public int Public;
            public int Compute(int x) => _a + x;
            public string Describe() => _b;
            public static int Total() => 0;
        }

        // Body-only edit: same fields, same order.
        private class BodyOnlyEdit
        {
            private int _a = 1;
            private string _b = "b";
            public int Public;
            public int Compute(int x) => _a * 100 + x;
            public string Describe() => "v2" + _b;
            public static int Total() => 1;
        }

        private class FieldInserted
        {
            private long _inserted = 0;
            private int _a = 1;
            private string _b = "b";
            public int Public;
            public int Compute(int x) => _a + x;
        }

        private class FieldRemoved
        {
            private int _a = 1;
            public int Public;
            public int Compute(int x) => _a + x;
        }

        private class FieldRetyped
        {
            private long _a = 1;
            private string _b = "b";
            public int Public;
            public int Compute(int x) => (int)_a + x;
        }

        private class SignatureChanged
        {
            private int _a = 1;
            private string _b = "b";
            public int Public;
            public long Compute(int x) => _a + x;   // return type differs
        }

        private class MethodAdded
        {
            private int _a = 1;
            private string _b = "b";
            public int Public;
            public int Compute(int x) => _a + x;
            public int BrandNew() => 42;
        }

        private static (List<SwapCandidate> candidates, List<SwapSkip> skips) Plan(
            Type compiled, Type loaded)
        {
            var candidates = new List<SwapCandidate>();
            var skips = new List<SwapSkip>();
            MethodSwapper.Plan(new[] { compiled }, _ => loaded, candidates, skips);
            return (candidates, skips);
        }

        [Test]
        public void BodyOnlyEdit_IsAllowed()
        {
            Assert.That(MethodSwapper.FieldLayoutMatches(typeof(Loaded), typeof(BodyOnlyEdit), out var reason),
                Is.True, reason);
        }

        [TestCase(typeof(FieldInserted), "field count", TestName = "InsertedField_ShiftsEverythingAfterIt")]
        [TestCase(typeof(FieldRemoved), "field count", TestName = "RemovedField_ShiftsEverythingAfterIt")]
        [TestCase(typeof(FieldRetyped), "field 0 changed", TestName = "RetypedField_ChangesTheOffsets")]
        public void LayoutChange_IsRefused(Type compiled, string expectedInReason)
        {
            var matches = MethodSwapper.FieldLayoutMatches(typeof(Loaded), compiled, out var reason);

            Assert.That(matches, Is.False, "a changed layout must never be swapped");
            Assert.That(reason, Does.Contain(expectedInReason));
        }

        [Test]
        public void LayoutRefusal_ExplainsWhyItMatters()
        {
            MethodSwapper.FieldLayoutMatches(typeof(Loaded), typeof(FieldInserted), out var reason);

            Assert.That(reason, Does.Contain("shifts"),
                "the caller should learn why adding a field is not a body-only edit");
        }

        [Test]
        public void Plan_PairsEveryMethodOfAnUnchangedLayout()
        {
            var (candidates, skips) = Plan(typeof(BodyOnlyEdit), typeof(Loaded));

            Assert.That(candidates.Select(c => c.Loaded.Name),
                Is.EquivalentTo(new[] { "Compute", "Describe", "Total" }),
                "instance and static methods alike");
            Assert.That(skips, Is.Empty);
        }

        [Test]
        public void Plan_SkipsTheWholeTypeWhenLayoutChanged()
        {
            var (candidates, skips) = Plan(typeof(FieldInserted), typeof(Loaded));

            Assert.That(candidates, Is.Empty, "no method of a relaid-out type may be swapped");
            Assert.That(skips.Single().Reason, Is.EqualTo(SkipReason.LayoutChanged));
        }

        [Test]
        public void Plan_SkipsTypesTheEditorHasNotLoaded()
        {
            var candidates = new List<SwapCandidate>();
            var skips = new List<SwapSkip>();

            MethodSwapper.Plan(new[] { typeof(BodyOnlyEdit) }, _ => null, candidates, skips);

            Assert.That(candidates, Is.Empty);
            Assert.That(skips.Single().Reason, Is.EqualTo(SkipReason.TypeNotLoaded));
            Assert.That(skips.Single().Detail, Does.Contain("recompile"),
                "a new type is not a hot-reload case and the caller needs to know");
        }

        [Test]
        public void Plan_SkipsAChangedReturnType()
        {
            var (candidates, skips) = Plan(typeof(SignatureChanged), typeof(Loaded));

            var compute = skips.SingleOrDefault(s => s.What.EndsWith(".Compute"));
            Assert.That(compute.Reason, Is.EqualTo(SkipReason.SignatureChanged));
            Assert.That(candidates.Any(c => c.Loaded.Name == "Compute"), Is.False);
        }

        [Test]
        public void Plan_SkipsMethodsThatDidNotExistBefore()
        {
            var (candidates, skips) = Plan(typeof(MethodAdded), typeof(Loaded));

            Assert.That(skips.Any(s => s.What.EndsWith(".BrandNew") && s.Reason == SkipReason.MethodNotFound),
                Is.True);
            Assert.That(candidates.Any(c => c.Loaded.Name == "Compute"), Is.True,
                "an added method must not stop the rest of the file from applying");
        }

        [Test]
        public void FieldLayoutMatches_HandlesNulls()
        {
            Assert.That(MethodSwapper.FieldLayoutMatches(null, typeof(Loaded), out _), Is.False);
            Assert.That(MethodSwapper.FieldLayoutMatches(typeof(Loaded), null, out _), Is.False);
        }
    }
}
