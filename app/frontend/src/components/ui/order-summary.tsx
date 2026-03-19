import { useState } from "react";
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
}

export function calculateOrderSummary(items: OrderItem[]): OrderSummaryProps {
    const total = items.reduce((sum, item) => sum + item.price * item.quantity, 0);
    const tax = total * 0.08; // 8% tax
    const finalTotal = total + tax;

    return {
        items,
        total,
        tax,
        finalTotal
    };
}

export default function OrderSummary({ order }: { order: OrderSummaryProps }) {
    const [isExpanded, setIsExpanded] = useState(true);
    const { items, total, tax, finalTotal } = order;

    return (
        <div className="rounded-3xl border border-[#285780]/20 bg-gradient-to-br from-white via-[#F2F8FA] to-[#FEDD00]/5 p-5 shadow-[0_20px_45px_rgba(40,87,128,0.12)] dark:border-white/15 dark:bg-gradient-to-br dark:from-[#0f1a24] dark:via-[#152231] dark:to-[#0f1a24]">
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
                    <div key={index} className="flex justify-between rounded-2xl bg-white/70 px-3 py-2 text-sm text-gray-700 shadow-sm dark:bg-white/5 dark:text-white">
                        <span className="font-semibold">
                            {item.display} {item.quantity > 1 && `(x${item.quantity})`}
                        </span>
                        <span className="font-mono text-[#E40046] dark:text-[#FF6B8A]">${(item.price * item.quantity).toFixed(2)}</span>
                    </div>
                ))}

                <div className="mt-4 space-y-2 border-t border-dashed border-primary/30 pt-4 dark:border-white/15">
                    <div className="flex justify-between text-sm text-gray-900 dark:text-white">
                        <span>Subtotal</span>
                        <span className="font-mono dark:text-white/90">${total.toFixed(2)}</span>
                    </div>
                    <div className="flex justify-between text-sm text-gray-900 dark:text-white">
                        <span>Tax (8%)</span>
                        <span className="font-mono dark:text-white/90">${tax.toFixed(2)}</span>
                    </div>
                </div>
            </div>
            <div className="mt-4 flex items-center justify-between rounded-2xl bg-white/90 px-4 py-3 text-lg font-semibold text-primary shadow-inner dark:bg-[#152231] dark:text-[#FF6B8A]">
                <span>Total Due</span>
                <span className="font-mono text-[#E40046] dark:text-[#FF6B8A]">${finalTotal.toFixed(2)}</span>
            </div>
        </div>
    );
}
