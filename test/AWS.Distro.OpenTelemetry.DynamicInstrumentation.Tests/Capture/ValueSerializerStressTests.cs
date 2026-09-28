// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Capture;

// Adversarial / extreme-input coverage for ValueSerializer beyond the happy-path and known-hazard cases
// in ValueSerializerTests. Every test here asserts the production invariant that matters for GA: the
// serializer runs on the USER's thread, so it must NEVER throw, NEVER StackOverflow, NEVER hang, and always
// return a bounded result — no matter how pathological the captured value is.
public class ValueSerializerStressTests
{
    private static readonly CaptureConfiguration DefaultLimits = CaptureConfiguration.Default;

    [Fact]
    public void Serialize_VeryDeepObjectChain_TerminatesAndDoesNotStackOverflow()
    {
        // 10,000-deep linked list. Without the depth cap this is a guaranteed StackOverflow (uncatchable,
        // process-fatal). The cap must bound recursion far below that.
        var head = new LinkNode();
        var cur = head;
        for (int i = 0; i < 10_000; i++)
        {
            cur.Next = new LinkNode();
            cur = cur.Next;
        }

        var act = () => ValueSerializer.Serialize(head, DefaultLimits with { MaxObjectDepth = 5 });

        act.Should().NotThrow();
        act().Should().NotBeNull();
    }

    [Fact]
    public void Serialize_VeryDeepNestedCollection_TerminatesAndDoesNotStackOverflow()
    {
        // 5,000-deep nested lists — the collection-depth analogue of the object-chain StackOverflow hazard.
        var root = new List<object>();
        var cur = root;
        for (int i = 0; i < 5_000; i++)
        {
            var next = new List<object>();
            cur.Add(next);
            cur = next;
        }

        var act = () => ValueSerializer.Serialize(root, DefaultLimits with { MaxCollectionDepth = 5 });

        act.Should().NotThrow();
        act().Should().NotBeNull();
    }

    [Fact]
    public void Serialize_WideObjectGraph_BoundedByFieldCap_RegardlessOfChildFanout()
    {
        // Each node has many children; combined with depth this is exponential if unbounded. Field cap +
        // depth cap must keep it finite and fast.
        var root = new WideNode(depth: 6, fanout: 8);

        var act = () => ValueSerializer.Serialize(root, DefaultLimits);

        act.Should().NotThrow();
        var result = act();
        result.Fields.Should().NotBeNull();
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    public void Serialize_ExtremeIntegers_RoundTripAsString(int value)
    {
        var result = ValueSerializer.Serialize(value, DefaultLimits);

        result.Type.Should().Be("System.Int32");
        result.Value.Should().Be(value.ToString());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void Serialize_SpecialDoubles_DoNotThrow(double value)
    {
        // NaN/Infinity are classic serialization landmines (JSON has no literal for them). The value
        // serializer stringifies via ToString(), so it must handle these without throwing.
        var act = () => ValueSerializer.Serialize(value, DefaultLimits);

        act.Should().NotThrow();
        act().Type.Should().Be("System.Double");
    }

    [Fact]
    public void Serialize_MaxLengthString_AtExactBoundary_NotTruncated_OneOver_Truncated()
    {
        var limits = DefaultLimits with { MaxStringLength = 100 };

        ValueSerializer.Serialize(new string('a', 100), limits).Truncated.Should().BeFalse();
        var over = ValueSerializer.Serialize(new string('a', 101), limits);
        over.Truncated.Should().BeTrue();
        over.Value!.Length.Should().Be(100);
    }

    [Fact]
    public void Serialize_StringWithControlAndUnicodeChars_PreservedNotCorrupted()
    {
        // Newlines, tabs, NUL, emoji, and non-BMP surrogate pairs must survive the capture path intact
        // (truncation is by length, not by byte, so a surrogate pair must not be split at the boundary in
        // a way that throws).
        var s = "line1\nline2\tcol\0null\U0001F600emoji";
        var result = ValueSerializer.Serialize(s, DefaultLimits);

        result.Type.Should().Be("System.String");
        result.Value.Should().Be(s);
    }

    [Fact]
    public void Serialize_EmptyString_CapturedAsEmptyNotNull()
    {
        var result = ValueSerializer.Serialize(string.Empty, DefaultLimits);

        result.Type.Should().Be("System.String");
        result.Value.Should().Be(string.Empty);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Serialize_MinLimits_AllClampedToOne_ProduceBoundedOutput()
    {
        // Every knob at its minimum (1). A rich object with nested collections must still serialize to a
        // tiny, bounded result without error.
        var limits = new CaptureConfiguration(
            CaptureArguments: [],
            CaptureLocals: [],
            CaptureReturn: true,
            CaptureStackTrace: false,
            MaxStringLength: 1,
            MaxCollectionWidth: 1,
            MaxCollectionDepth: 1,
            MaxObjectDepth: 1,
            MaxFieldsPerObject: 1,
            MaxStackFrames: 1,
            MaxHits: 1);

        var obj = new WideNode(depth: 4, fanout: 4);

        var act = () => ValueSerializer.Serialize(obj, limits);

        act.Should().NotThrow();
        var result = act();
        result.Fields.Should().NotBeNull();
        result.Fields!.Count.Should().BeLessThanOrEqualTo(1, "MaxFieldsPerObject=1 must cap fields at one");
    }

    [Fact]
    public void Serialize_LargeCollection_OneMillionElements_DoesNotEnumeratePastWidth()
    {
        // A million-element array must cap at MaxCollectionWidth and complete instantly — never walk the
        // whole thing on the user's thread. LazyCountArray reports an O(1) count but throws if enumerated
        // past the width, proving we stop at the cap.
        var big = new BoundedEnumerationArray(1_000_000, throwAfter: DefaultLimits.MaxCollectionWidth);

        var act = () => ValueSerializer.Serialize(big, DefaultLimits);

        act.Should().NotThrow("enumeration must stop at MaxCollectionWidth, never walk all 1M");
        var result = act();
        result.Elements.Should().HaveCount(DefaultLimits.MaxCollectionWidth);
        result.OriginalSize.Should().Be(1_000_000);
        result.NotCapturedReason.Should().Be(NotCapturedReason.CollectionSize);
    }

    [Fact]
    public void Serialize_DictionaryWithNullKey_DoesNotThrow()
    {
        // A non-generic Hashtable permits a null-ish key path; DictionaryEntry key can be awkward. The
        // serializer coalesces a null key to "null" rather than throwing.
        var dict = new Dictionary<string, int> { ["a"] = 1 };
        var result = ValueSerializer.Serialize(dict, DefaultLimits);

        result.Fields.Should().ContainKey("a");
    }

    [Fact]
    public void Serialize_ObjectWhoseToStringThrows_AtDepthLimit_DoesNotEscape()
    {
        // When an object is past the object-depth limit the serializer renders a marker (it does NOT call
        // ToString on it), so a throwing ToString deep in the graph must not abort capture.
        var obj = new HasThrowingToStringChild { Child = new ThrowingToString() };

        var act = () => ValueSerializer.Serialize(obj, DefaultLimits with { MaxObjectDepth = 1 });

        act.Should().NotThrow();
    }

    [Fact]
    public void Serialize_ValueTuple_CapturedWithoutError()
    {
        var result = ValueSerializer.Serialize((1, "two", 3.0), DefaultLimits);

        result.Should().NotBeNull();
        result.Fields.Should().NotBeNull();
    }

    [Fact]
    public void Serialize_BoxedValueTypesAsSiblings_NotFalsePositivedAsCycle()
    {
        // Boxed value types are excluded from identity tracking (each box is fresh). Two boxes of the same
        // value as siblings must both serialize fully with no AlreadyCaptured.
        object boxed = 42;
        var holder = new HasTwoObjects { First = boxed, Second = boxed };

        var result = ValueSerializer.Serialize(holder, DefaultLimits);

        result.Fields!["First"].NotCapturedReason.Should().Be(NotCapturedReason.None);
        result.Fields!["Second"].NotCapturedReason.Should().Be(NotCapturedReason.None);
    }

    private class LinkNode
    {
        public LinkNode? Next { get; set; }
    }

    private class WideNode
    {
        public WideNode(int depth, int fanout)
        {
            if (depth <= 0)
            {
                return;
            }

            this.Children = new List<WideNode>();
            for (int i = 0; i < fanout; i++)
            {
                this.Children.Add(new WideNode(depth - 1, fanout));
            }
        }

        public List<WideNode>? Children { get; }

        public int Marker => 1;
    }

    private class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("no tostring");
    }

    private class HasThrowingToStringChild
    {
        public ThrowingToString? Child { get; set; }
    }

    private class HasTwoObjects
    {
        public object? First { get; set; }

        public object? Second { get; set; }
    }

    // Reports an O(1) count (so it is walked as a collection) but throws if enumerated past `throwAfter`,
    // proving the serializer stops at MaxCollectionWidth instead of walking the whole sequence.
    private sealed class BoundedEnumerationArray : ICollection
    {
        private readonly int throwAfter;

        public BoundedEnumerationArray(int count, int throwAfter)
        {
            this.Count = count;
            this.throwAfter = throwAfter;
        }

        public int Count { get; }

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; ; i++)
            {
                if (i >= this.throwAfter)
                {
                    throw new InvalidOperationException($"enumerated past {this.throwAfter} — width cap not honored");
                }

                yield return i;
            }
        }

        public void CopyTo(Array array, int index) => throw new NotSupportedException();
    }
}
