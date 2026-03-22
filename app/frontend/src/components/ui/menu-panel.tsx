import menuItemsData from "@/data/menuItems.json";
import { memo, useCallback, useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { ChevronDown } from "lucide-react";

interface Size {
    size: string;
    price: number;
}

interface MenuItem {
    name: string;
    sizes: Size[];
    description: string;
}

interface MenuCategory {
    category: string;
    items: MenuItem[];
}

const categoryIcons: Record<string, string> = {
    "Burgers & Sandwiches": "🍔",
    "Shakes & Ice Cream": "🥤",
    "Slushes & Drinks": "🧊",
    "Hot Dogs & Tots": "🌭",
    "Combos": "🍟",
    Extras: "✨"
};

const menuItems = menuItemsData.menuItems as MenuCategory[];

// All categories expanded by default
const initialExpanded = new Set<string>(menuItems.map(c => c.category));

export default memo(function MenuPanel() {
    const [expanded, setExpanded] = useState<Set<string>>(() => new Set(initialExpanded));

    const toggle = useCallback((category: string) => {
        setExpanded(prev => {
            const next = new Set(prev);
            if (next.has(category)) {
                next.delete(category);
            } else {
                next.add(category);
            }
            return next;
        });
    }, []);

    return (
        <div className="space-y-4">
            {menuItems.map(category => {
                const isOpen = expanded.has(category.category);
                return (
                    <div
                        key={category.category}
                        className="rounded-3xl border border-primary/10 bg-white/80 shadow-[0_15px_35px_rgba(40,87,128,0.08)] dark:border-white/10 dark:bg-[#0f1a24]/95 dark:shadow-[0_25px_55px_rgba(0,0,0,0.65)]"
                    >
                        <button
                            type="button"
                            onClick={() => toggle(category.category)}
                            className="flex w-full cursor-pointer items-center justify-between gap-3 p-4"
                            aria-expanded={isOpen}
                        >
                            <div className="flex items-center gap-2 sm:gap-3">
                                <span className="text-2xl" aria-hidden>
                                    {categoryIcons[category.category] ?? "🍹"}
                                </span>
                                <h3 className="break-keep text-left font-semibold uppercase tracking-wide text-primary dark:text-primary">
                                    {category.category}
                                </h3>
                            </div>
                            <div className="flex items-center gap-2">
                                <span className="whitespace-nowrap rounded-full bg-[#285780]/10 px-3 py-1 text-xs font-bold text-[#285780] dark:bg-[#152231] dark:text-[#74D2E7]">
                                    {category.items.length} items
                                </span>
                                <motion.span
                                    animate={{ rotate: isOpen ? 180 : 0 }}
                                    transition={{ duration: 0.2 }}
                                    className="text-primary/60 dark:text-white/50"
                                >
                                    <ChevronDown size={18} />
                                </motion.span>
                            </div>
                        </button>

                        <AnimatePresence initial={false}>
                            {isOpen && (
                                <motion.div
                                    initial={{ height: 0, opacity: 0 }}
                                    animate={{ height: "auto", opacity: 1 }}
                                    exit={{ height: 0, opacity: 0 }}
                                    transition={{ duration: 0.25, ease: "easeInOut" }}
                                    className="overflow-hidden"
                                >
                                    <div className="space-y-4 px-4 pb-4">
                                        {category.items.map(item => (
                                            <div
                                                key={item.name}
                                                className="rounded-2xl border border-dashed border-primary/20 bg-white/70 p-3 transition-colors dark:border-white/10 dark:bg-white/5"
                                            >
                                                <div className="flex flex-wrap items-baseline justify-between gap-2">
                                                    <div className="pr-1">
                                                        <span className="font-semibold text-foreground dark:text-white">{item.name}</span>
                                                        <p className="text-sm text-muted-foreground">{item.description}</p>
                                                    </div>
                                                    <div className="text-right">
                                                        {item.sizes.map(({ size, price }) => (
                                                            <div key={size} className="font-mono text-sm text-foreground/80 dark:text-white/80">
                                                                {size !== "standard" ? <span className="capitalize">{`${size}: `}</span> : null}
                                                                <span>${price.toFixed(2)}</span>
                                                            </div>
                                                        ))}
                                                    </div>
                                                </div>
                                            </div>
                                        ))}
                                    </div>
                                </motion.div>
                            )}
                        </AnimatePresence>
                    </div>
                );
            })}
        </div>
    );
});
