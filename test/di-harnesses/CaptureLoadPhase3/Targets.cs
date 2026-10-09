// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// Payload shapes mirroring ADOT Java DI Performance Test Results (March 2026) Phase 3, so the
// .NET numbers line up test-for-test with Java's. Java's tests 3.1-3.5 captured only simple
// string args/returns (hence their near-identical ~2.3KB snapshots); 3.6-3.7 returned real
// object graphs and are where their overhead exploded (83% throughput loss, 25KB snapshots).
namespace CaptureLoad.Targets;

// ---- Java 3.6 analogue: SimpleData object return (depth=1) ----
public sealed class SimpleData
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Value { get; set; }

    public bool Active { get; set; }

    public double Score { get; set; }
}

// ---- Java 3.7 analogue: nested Order -> Customer -> Address (depth=3) ----
// This is the shape that produced Java's worst case: 25,314-byte snapshots, 83.4% throughput
// loss, 25.7% capture rate (74.3% silently dropped).
public sealed class Address
{
    public string Street { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Zip { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;
}

public sealed class Customer
{
    public string CustomerId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public Address ShippingAddress { get; set; } = new();

    public Address BillingAddress { get; set; } = new();
}

public sealed class OrderLine
{
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public double UnitPrice { get; set; }
}

public sealed class Order
{
    public string OrderId { get; set; } = string.Empty;

    public double Total { get; set; }

    public Customer Customer { get; set; } = new();

    public List<OrderLine> Lines { get; set; } = new();
}

/// <summary>
/// The methods a probe is placed on. Bodies are deliberately trivial so that measured cost is
/// attributable to the DI capture path, not to the business logic.
/// </summary>
public sealed class CaptureComplexityTargets
{
    // Java 3.1 — primitives only.
    public string PrimitivesOnly(int n) => $"Processed: {n}";

    // Java 3.2 — simple string in, string out (the "minimal capture" shape).
    public string SimpleObject(string s) => $"Processed: {s.ToUpperInvariant()}";

    // Java 3.6 — returns a flat object (depth=1).
    public SimpleData CreateSimpleData(string id) => new()
    {
        Id = id,
        Name = $"name-{id}",
        Value = id.Length * 7,
        Active = true,
        Score = 99.5,
    };

    // Java 3.7 — returns a nested graph (depth=3). Java's worst case.
    public Order CreateOrder(string orderId, double total)
    {
        var addr = new Address
        {
            Street = "410 Terry Ave N", City = "Seattle", State = "WA", Zip = "98109", Country = "US",
        };
        return new Order
        {
            OrderId = orderId,
            Total = total,
            Customer = new Customer
            {
                CustomerId = $"cust-{orderId}",
                Email = "customer@example.com",
                FullName = "Example Customer",
                ShippingAddress = addr,
                BillingAddress = addr,
            },
            Lines = new List<OrderLine>
            {
                new() { Sku = "SKU-1", Quantity = 2, UnitPrice = 19.99 },
                new() { Sku = "SKU-2", Quantity = 1, UnitPrice = 49.50 },
                new() { Sku = "SKU-3", Quantity = 5, UnitPrice = 3.25 },
            },
        };
    }

    // Java 3.4 — large collection (100 items).
    public List<string> LargeList(int size)
    {
        var list = new List<string>(size);
        for (int i = 0; i < size; i++)
        {
            list.Add($"item-{i}-{Guid.NewGuid():N}");
        }

        return list;
    }
}
