// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Capture;

public class ValueSerializerTests
{
    private static readonly CaptureConfiguration DefaultLimits = CaptureConfiguration.Default;

    [Fact]
    public void Serialize_Null_ReturnsNullType()
    {
        var result = ValueSerializer.Serialize(null, DefaultLimits);

        result.Type.Should().Be("null");
        result.Value.Should().Be("null");
    }

    [Fact]
    public void Serialize_Int_ReturnsPrimitive()
    {
        var result = ValueSerializer.Serialize(42, DefaultLimits);

        result.Type.Should().Be("System.Int32");
        result.Value.Should().Be("42");
    }

    [Fact]
    public void Serialize_Bool_ReturnsPrimitive()
    {
        var result = ValueSerializer.Serialize(true, DefaultLimits);

        result.Type.Should().Be("System.Boolean");
        result.Value.Should().Be("True");
    }

    [Fact]
    public void Serialize_Double_ReturnsPrimitive()
    {
        var result = ValueSerializer.Serialize(3.14, DefaultLimits);

        result.Type.Should().Be("System.Double");
        result.Value.Should().Be("3.14");
    }

    [Fact]
    public void Serialize_ShortString_ReturnsFullValue()
    {
        var result = ValueSerializer.Serialize("hello", DefaultLimits);

        result.Type.Should().Be("System.String");
        result.Value.Should().Be("hello");
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Serialize_LongString_Truncates()
    {
        var longStr = new string('x', 500);
        var result = ValueSerializer.Serialize(longStr, DefaultLimits);

        result.Value!.Length.Should().Be(255);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Serialize_CustomMaxStringLength_Respected()
    {
        var limits = DefaultLimits with { MaxStringLength = 10 };
        var result = ValueSerializer.Serialize("hello world, this is long", limits);

        result.Value!.Length.Should().Be(10);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Serialize_List_ReturnsElements()
    {
        var list = new List<int> { 1, 2, 3 };
        var result = ValueSerializer.Serialize(list, DefaultLimits);

        result.Elements.Should().HaveCount(3);
        result.Elements![0].Value.Should().Be("1");
        result.Elements[1].Value.Should().Be("2");
        result.Elements[2].Value.Should().Be("3");
        result.OriginalSize.Should().BeNull(); // not truncated
    }

    [Fact]
    public void Serialize_LargeList_TruncatesToMaxWidth()
    {
        var list = Enumerable.Range(1, 50).ToList();
        var result = ValueSerializer.Serialize(list, DefaultLimits);

        result.Elements.Should().HaveCount(20); // MaxCollectionWidth = 20
        result.OriginalSize.Should().Be(50);
        result.NotCapturedReason.Should().Be(NotCapturedReason.CollectionSize);
    }

    [Fact]
    public void Serialize_LazyEnumerable_StopsAtMaxWidth()
    {
        // A non-ICollection lazy sequence must NOT be walked in full (it may be infinite and this
        // runs on the user's thread). We enumerate at most MaxCollectionWidth+1: width captured, +1 to
        // detect truncation.
        int pulled = 0;
        IEnumerable<int> Lazy()
        {
            while (true)
            {
                pulled++;
                yield return pulled;
            }
        }

        var result = ValueSerializer.Serialize(Lazy(), DefaultLimits);

        result.Elements.Should().HaveCount(20); // MaxCollectionWidth = 20
        result.NotCapturedReason.Should().Be(NotCapturedReason.CollectionSize);
        result.OriginalSize.Should().Be(21, "unknown-size sequences report at-least width+1, not an exact walked count");
        pulled.Should().Be(21, "the infinite sequence must be pulled only width+1 times, never fully enumerated");
    }

    [Fact]
    public void Serialize_Dictionary_ReturnsFields()
    {
        var dict = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        var result = ValueSerializer.Serialize(dict, DefaultLimits);

        result.Fields.Should().ContainKey("a");
        result.Fields.Should().ContainKey("b");
        result.Fields!["a"].Value.Should().Be("1");
    }

    [Fact]
    public void Serialize_LargeDictionary_TruncatesToMaxWidth()
    {
        var dict = Enumerable.Range(1, 50).ToDictionary(i => $"key{i}", i => i);
        var result = ValueSerializer.Serialize(dict, DefaultLimits);

        result.Fields.Should().HaveCount(20);
        result.OriginalSize.Should().Be(50);
        result.NotCapturedReason.Should().Be(NotCapturedReason.CollectionSize);
    }

    [Fact]
    public void Serialize_Object_CapturesPublicFieldsAndProperties()
    {
        var obj = new TestObj { Name = "Alice", Age = 30 };
        var result = ValueSerializer.Serialize(obj, DefaultLimits);

        result.Type.Should().Contain("TestObj");
        result.Fields.Should().ContainKey("Name");
        result.Fields.Should().ContainKey("Age");
        result.Fields!["Name"].Value.Should().Be("Alice");
        result.Fields!["Age"].Value.Should().Be("30");
    }

    [Fact]
    public void Serialize_DepthLimit_StopsRecursion()
    {
        var limits = DefaultLimits with { MaxObjectDepth = 1 };
        var obj = new Nested { Inner = new Nested { Inner = new Nested() } };

        var result = ValueSerializer.Serialize(obj, limits);

        result.Fields.Should().ContainKey("Inner");
        result.Fields!["Inner"].Value.Should().Contain("depth limit");
        result.Fields!["Inner"].NotCapturedReason.Should().Be(NotCapturedReason.Depth);
    }

    [Fact]
    public void Serialize_MaxFieldsPerObject_Respected()
    {
        var limits = DefaultLimits with { MaxFieldsPerObject = 1 };
        var obj = new TestObj { Name = "Alice", Age = 30 };

        var result = ValueSerializer.Serialize(obj, limits);

        result.Fields.Should().HaveCount(1);
        result.NotCapturedReason.Should().Be(NotCapturedReason.FieldCount);
    }

    [Fact]
    public void Serialize_Array_ReturnsElements()
    {
        var arr = new[] { "a", "b", "c" };
        var result = ValueSerializer.Serialize(arr, DefaultLimits);

        result.Elements.Should().HaveCount(3);
        result.Elements![0].Value.Should().Be("a");
    }

    [Fact]
    public void Serialize_Enum_ReturnsPrimitive()
    {
        var result = ValueSerializer.Serialize(DayOfWeek.Monday, DefaultLimits);

        result.Type.Should().Be("System.DayOfWeek");
        result.Value.Should().Be("Monday");
    }

    [Fact]
    public void Serialize_SelfReferencingObject_TerminatesViaDepthLimit()
    {
        // Production hazard: a cyclic object graph must not StackOverflow. The depth
        // limit must bound the recursion.
        var a = new Nested();
        a.Inner = a; // direct self-cycle

        var act = () => ValueSerializer.Serialize(a, DefaultLimits with { MaxObjectDepth = 3 });

        act.Should().NotThrow();
        var result = act();
        result.Should().NotBeNull();
    }

    [Fact]
    public void Serialize_MutualCycle_Terminates()
    {
        var a = new Nested();
        var b = new Nested();
        a.Inner = b;
        b.Inner = a; // A <-> B cycle

        var act = () => ValueSerializer.Serialize(a, DefaultLimits with { MaxObjectDepth = 5 });

        act.Should().NotThrow();
    }

    [Fact]
    public void Serialize_DirectSelfCycle_ReportsAlreadyCaptured()
    {
        // Reference-cycle detection fires on identity before the depth cap: a's Inner is a itself,
        // so the nested reference is marked AlreadyCaptured rather than recursed into.
        var a = new Nested();
        a.Inner = a;

        var result = ValueSerializer.Serialize(a, DefaultLimits with { MaxObjectDepth = 5 });

        result.Fields!["Inner"].NotCapturedReason.Should().Be(NotCapturedReason.AlreadyCaptured);
        result.Fields!["Inner"].Fields.Should().BeNull("the cycle is cut, not recursed");
    }

    [Fact]
    public void Serialize_SameObjectAsSiblings_NotFalsePositivedAsCycle()
    {
        // The same object referenced twice as SIBLINGS (not ancestry) must be captured both times —
        // the visited set removes the reference on unwind so siblings aren't flagged as cycles.
        var shared = new Nested();
        var parent = new HasTwo { First = shared, Second = shared };

        var result = ValueSerializer.Serialize(parent, DefaultLimits with { MaxObjectDepth = 5 });

        result.Fields!["First"].NotCapturedReason.Should().Be(NotCapturedReason.None);
        result.Fields!["Second"].NotCapturedReason.Should().Be(NotCapturedReason.None);
    }

    [Fact]
    public void Serialize_NullElementsInCollection_HandledGracefully()
    {
        var list = new List<string?> { "a", null, "c" };

        var result = ValueSerializer.Serialize(list, DefaultLimits);

        result.Elements.Should().HaveCount(3);
        result.Elements![1].Type.Should().Be("null");
    }

    [Fact]
    public void Serialize_NullValuesInDictionary_HandledGracefully()
    {
        var dict = new Dictionary<string, string?> { ["present"] = "v", ["missing"] = null };

        var result = ValueSerializer.Serialize(dict, DefaultLimits);

        result.Fields!["missing"].Type.Should().Be("null");
    }

    [Fact]
    public void Serialize_PropertyThatThrows_CapturesAccessErrorNotCrash()
    {
        var obj = new ThrowingProp();

        var act = () => ValueSerializer.Serialize(obj, DefaultLimits);

        act.Should().NotThrow();
        var result = act();
        result.Fields.Should().ContainKey(nameof(ThrowingProp.Bad));
        result.Fields![nameof(ThrowingProp.Bad)].Value.Should().Contain("access error");
    }

    [Fact]
    public void Serialize_EmptyCollection_ReturnsEmptyElements()
    {
        var result = ValueSerializer.Serialize(new List<int>(), DefaultLimits);

        result.Elements.Should().NotBeNull();
        result.Elements.Should().BeEmpty();
        result.OriginalSize.Should().BeNull();
    }

    [Fact]
    public void Serialize_NestedCollection_RespectsDepth()
    {
        // A list of lists must not recurse unbounded.
        var inner = new List<object> { 1, 2 };
        var outer = new List<object> { inner, inner };

        var act = () => ValueSerializer.Serialize(outer, DefaultLimits with { MaxObjectDepth = 2 });

        act.Should().NotThrow();
        act().Elements.Should().HaveCount(2);
    }

    [Fact]
    public void Serialize_MaxCollectionDepth_BoundsNestedCollections()
    {
        // Regression: MaxCollectionDepth used to be parsed/clamped/stored but never read —
        // collection nesting was silently bounded by MaxObjectDepth instead. This proves the
        // knob is authoritative: a deeply nested list is cut at MaxCollectionDepth regardless
        // of a generous MaxObjectDepth.
        var depth4 = new List<object> { new List<object> { new List<object> { new List<object> { 1 } } } };
        var limits = DefaultLimits with { MaxCollectionDepth = 2, MaxObjectDepth = 5 };

        var result = ValueSerializer.Serialize(depth4, limits);

        // depth 0 (outer) -> depth 1 (inner) -> depth 2 hits the limit, rendered as marker.
        var level1 = result.Elements!.Single();
        var level2 = level1.Elements!.Single();
        level2.Value.Should().Contain("depth limit");
        level2.Elements.Should().BeNull();
    }

    [Fact]
    public void Serialize_SelfReferencingCollection_TerminatesViaCollectionDepth()
    {
        // A collection that contains itself must terminate on MaxCollectionDepth, not
        // StackOverflow.
        var list = new List<object>();
        list.Add(list);

        var act = () => ValueSerializer.Serialize(list, DefaultLimits with { MaxCollectionDepth = 3 });

        act.Should().NotThrow();
    }

    [Fact]
    public void Serialize_TruncationBoundary_ExactlyMaxLength_NotTruncated()
    {
        var exact = new string('y', 255);
        var result = ValueSerializer.Serialize(exact, DefaultLimits);

        result.Truncated.Should().BeFalse();
        result.Value!.Length.Should().Be(255);
    }

    private class TestObj
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    private class Nested
    {
        public Nested? Inner { get; set; }
    }

    private class HasTwo
    {
        public Nested? First { get; set; }

        public Nested? Second { get; set; }
    }

    private class ThrowingProp
    {
        public string Bad => throw new InvalidOperationException("no access");
    }
}
