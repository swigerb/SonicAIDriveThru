import { useState, memo } from "react";
import { ChevronDown, ChevronUp } from "lucide-react";

export interface OrderItem {
    item: string;
    size: string;
    quantity: number;
    price: number;
    display: string;
}

export interface OrderSummaryProps {
    items: OrderItem[];
    total: number;
    tax: number;
    finalTotal: number;
    // #47: optional, additive display strings the backend computes with the exact-decimal
    // ROUND_HALF_UP contract rule (app/backend/money_utils.py::format_money) before any float
    // conversion happens. When present these are the single source of truth for what the ticket
    // shows -- prefer them over re-rounding the numeric fields with `.toFixed`, which cannot tell
    // apart e.g. 88.04499999999999 from 88.045 (both meant to be exactly $88.045) once the value
    // has already degraded into a noisy IEEE-754 double.
    totalDisplay?: string;
    taxDisplay?: string;
    finalTotalDisplay?: string;
}

/**
 * Formats a money value as an exact "$0.00" string, robust to the floating-point noise that
 * `.toFixed(2)` alone cannot correct (#47). `.toFixed(2)` rounds directly off the noisy double,
 * so two numbers that both represent the same intended decimal (e.g. `88.04499999999999` and
 * `88.045`, both meant to be $88.045) can render two different cents. Rounding first to a much
 * higher, but still safely-representable, number of decimal places collapses that noise (which
 * only ever appears past the 10th-or-so decimal place for these magnitudes) back to the clean
 * decimal before the final two-place rounding — so both inputs land on the same "$88.05".
 *
 * This is a client-side safety net for values that never went through the backend's exact-Decimal
 * pipeline (the dummy-data preview and each line item's `price * quantity`). Whenever the backend
 * has already supplied a `*Display` string (see `OrderSummaryProps`), prefer that string instead —
 * it's derived from the exact Decimal before any float conversion at all, which is strictly more
 * reliable than any client-side float correction can be.
 */
export function formatMoney(value: number): string {
    const cleaned = Number(value.toFixed(10));
    return `$${cleaned.toFixed(2)}`;
}

export function calculateOrderSummary(items: OrderItem[]): OrderSummaryProps {
    const total = items.reduce((sum, item) => sum + item.price * item.quantity, 0);
    const tax = total * 0.08; // 8% tax
    const finalTotal = total + tax;

    return {
        items,
        total,
        tax,
        finalTotal,
        totalDisplay: formatMoney(total),
        taxDisplay: formatMoney(tax),
        finalTotalDisplay: formatMoney(finalTotal)
    };
}

const OrderItemRow = memo(function OrderItemRow({ item }: { item: OrderItem }) {
    return (
        <div className="flex justify-between rounded-2xl bg-white/70 px-3 py-2 text-sm text-gray-700 shadow-xs dark:bg-white/5 dark:text-white">
            <span className="font-semibold">
                {item.display} {item.quantity > 1 && `(x${item.quantity})`}
            </span>
            <span className="font-mono text-[#E40046] dark:text-[#FF6B8A]">{formatMoney(item.price * item.quantity)}</span>
        </div>
    );
});

export default memo(function OrderSummary({ order }: { order: OrderSummaryProps }) {
    const [isExpanded, setIsExpanded] = useState(true);
    const { items, total, tax, finalTotal, totalDisplay, taxDisplay, finalTotalDisplay } = order;

    return (
        <div className="rounded-3xl border border-[#285780]/20 bg-linear-to-br from-white via-[#F2F8FA] to-[#FEDD00]/5 p-5 shadow-[0_20px_45px_rgba(40,87,128,0.12)] dark:border-white/15 dark:bg-linear-to-br dark:from-[#0f1a24] dark:via-[#152231] dark:to-[#0f1a24]">
            <div className="mb-4 flex items-center justify-between">
                <div>
                    <p className="text-xs font-bold uppercase tracking-[0.3em] text-[#E40046] dark:text-[#FF6B8A]">Carhop ticket</p>
                    <h2 className="text-2xl font-black text-[#E40046] dark:text-[#FF6B8A]">Your Sonic Order</h2>
                </div>
                <button onClick={() => setIsExpanded(!isExpanded)} className="flex items-center text-sm text-gray-500 dark:text-gray-300 md:hidden">
                    {isExpanded ? (
                        <>
                            Less <ChevronUp className="ml-1 h-4 w-4" />
                        </>
                    ) : (
                        <>
                            More <ChevronDown className="ml-1 h-4 w-4" />
                        </>
                    )}
                </button>
            </div>
            <div className={`space-y-2 ${isExpanded ? "block" : "hidden md:block"}`}>
                {items.length === 0 && <p className="text-sm text-muted-foreground dark:text-white/70">Add a slush, burger, or shake to kick things off.</p>}
                {items.map((item, index) => (
                    <OrderItemRow key={index} item={item} />
                ))}

                <div className="mt-4 space-y-2 border-t border-dashed border-primary/30 pt-4 dark:border-white/15">
                    <div className="flex justify-between text-sm text-gray-900 dark:text-white">
                        <span>Subtotal</span>
                        <span className="font-mono dark:text-white/90">{totalDisplay ?? formatMoney(total)}</span>
                    </div>
                    <div className="flex justify-between text-sm text-gray-900 dark:text-white">
                        <span>Tax (8%)</span>
                        <span className="font-mono dark:text-white/90">{taxDisplay ?? formatMoney(tax)}</span>
                    </div>
                </div>
            </div>
            <div className="mt-4 flex items-center justify-between rounded-2xl bg-white/90 px-4 py-3 text-lg font-semibold text-primary shadow-inner dark:bg-[#152231] dark:text-[#FF6B8A]">
                <span>Total Due</span>
                <span className="font-mono text-[#E40046] dark:text-[#FF6B8A]">{finalTotalDisplay ?? formatMoney(finalTotal)}</span>
            </div>
        </div>
    );
});
