import { calculateOrderSummary, OrderItem } from "../order-summary";

describe("calculateOrderSummary", () => {
    it("returns zeros for an empty item list", () => {
        const result = calculateOrderSummary([]);
        expect(result.items).toHaveLength(0);
        expect(result.total).toBe(0);
        expect(result.tax).toBe(0);
        expect(result.finalTotal).toBe(0);
    });

    it("computes correct totals for a single item", () => {
        const items: OrderItem[] = [
            { item: "Large Tots", size: "standard", quantity: 1, price: 3.29, display: "Large Tots" }
        ];
        const result = calculateOrderSummary(items);
        expect(result.total).toBeCloseTo(3.29);
        expect(result.tax).toBeCloseTo(3.29 * 0.08);
        expect(result.finalTotal).toBeCloseTo(3.29 * 1.08);
    });

    it("computes correct totals for multiple items with quantities", () => {
        const items: OrderItem[] = [
            { item: "Cherry Limeade", size: "medium", quantity: 2, price: 2.99, display: "Medium Cherry Limeade" },
            { item: "Chili Cheese Coney", size: "standard", quantity: 3, price: 3.99, display: "Chili Cheese Coney" }
        ];
        const expectedTotal = 2 * 2.99 + 3 * 3.99;
        const result = calculateOrderSummary(items);
        expect(result.total).toBeCloseTo(expectedTotal);
        expect(result.tax).toBeCloseTo(expectedTotal * 0.08);
        expect(result.finalTotal).toBeCloseTo(expectedTotal * 1.08);
    });

    it("handles zero-price items", () => {
        const items: OrderItem[] = [
            { item: "Free Sample", size: "small", quantity: 1, price: 0, display: "Small Free Sample" }
        ];
        const result = calculateOrderSummary(items);
        expect(result.total).toBe(0);
        expect(result.finalTotal).toBe(0);
    });
});
