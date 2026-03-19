import { render, screen } from "@testing-library/react";
import OrderSummary, { calculateOrderSummary, OrderItem, OrderSummaryProps } from "../order-summary";

describe("OrderSummary", () => {
    const sampleItems: OrderItem[] = [
        { item: "SuperSONIC® Double Cheeseburger", size: "standard", quantity: 2, price: 6.99, display: "SuperSONIC® Double Cheeseburger" },
        { item: "Large Tots", size: "standard", quantity: 1, price: 3.29, display: "Large Tots" }
    ];

    it("renders Sonic items with the correct totals", () => {
        const summary = calculateOrderSummary(sampleItems);
        render(<OrderSummary order={summary} />);

        expect(screen.getByText("Your Sonic Order")).toBeInTheDocument();
        expect(screen.getByText(/SuperSONIC® Double Cheeseburger/)).toBeInTheDocument();
        expect(screen.getByText(/Large Tots/)).toBeInTheDocument();
        expect(screen.getByText(`$${summary.total.toFixed(2)}`)).toBeInTheDocument();
        expect(screen.getByText(`$${summary.finalTotal.toFixed(2)}`)).toBeInTheDocument();
    });

    it("shows the empty-state helper when no items are present", () => {
        const emptySummary: OrderSummaryProps = { items: [], total: 0, tax: 0, finalTotal: 0 };
        render(<OrderSummary order={emptySummary} />);

        expect(screen.getByText(/Add a slush, burger, or shake/i)).toBeInTheDocument();
    });
});
