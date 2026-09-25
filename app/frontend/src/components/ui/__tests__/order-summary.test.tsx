import { render, screen } from "@testing-library/react";
import OrderSummary, { calculateOrderSummary, formatMoney, OrderItem, OrderSummaryProps } from "../order-summary";

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

    // #47: Rick's two repro values are distinct IEEE-754 doubles that both mean the same exact
    // decimal ($88.045, which rounds up to $88.05 per the ROUND_HALF_UP contract). Plain
    // `.toFixed(2)` renders them inconsistently ($88.04 vs $88.05); formatMoney must not.
    it.each([["88.04499999999999", 88.04499999999999], ["88.045", 88.045]])(
        "renders %s as $88.05 via formatMoney",
        (_label, value) => {
            expect(formatMoney(value)).toBe("$88.05");
        }
    );

    it("renders both of Rick's repro totals identically as $88.05 with no backend display string", () => {
        const values = [88.04499999999999, 88.045];
        for (const finalTotal of values) {
            const summary: OrderSummaryProps = { items: [], total: finalTotal, tax: 0, finalTotal };
            const { unmount } = render(<OrderSummary order={summary} />);
            expect(screen.getAllByText("$88.05")).toHaveLength(2); // Subtotal row + Total Due row
            unmount();
        }
    });

    it("prefers the backend-supplied *Display strings over recomputing from the numeric fields", () => {
        // Even if the numeric `finalTotal` would format differently on its own, the backend's
        // exact-Decimal-derived display string is the single source of truth and must win.
        const summary: OrderSummaryProps = {
            items: [],
            total: 5.265,
            tax: 0,
            finalTotal: 5.265,
            totalDisplay: "$5.27",
            taxDisplay: "$0.00",
            finalTotalDisplay: "$5.27"
        };
        render(<OrderSummary order={summary} />);

        expect(screen.getAllByText("$5.27")).toHaveLength(2); // Subtotal row + Total Due row
        expect(screen.getByText("$0.00")).toBeInTheDocument();
    });

    it("uses formatMoney (not raw .toFixed) for per-item line prices", () => {
        const items: OrderItem[] = [
            { item: "Route 44 Drink", size: "route44", quantity: 1, price: 88.04499999999999, display: "Route 44 Drink" }
        ];
        const summary: OrderSummaryProps = { items, total: 88.04499999999999, tax: 0, finalTotal: 88.04499999999999 };
        render(<OrderSummary order={summary} />);

        // line item + Subtotal + Total Due all agree, since no *Display strings were supplied.
        expect(screen.getAllByText("$88.05")).toHaveLength(3);
    });
});
