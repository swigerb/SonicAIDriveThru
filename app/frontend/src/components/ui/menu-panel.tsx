import menuItemsData from "@/data/menuItems.json";

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

export default function MenuPanel() {

    return (
        <div className="space-y-8">
            {menuItems.map(category => (
                <div
                    key={category.category}
                    className="rounded-3xl border border-primary/10 bg-white/80 p-4 shadow-[0_15px_35px_rgba(40,87,128,0.08)] dark:border-white/10 dark:bg-[#0f1a24]/95 dark:shadow-[0_25px_55px_rgba(0,0,0,0.65)]"
                >
                    <div className="mb-4 flex items-center justify-between gap-3">
                        <div className="flex flex-wrap items-center gap-2 sm:flex-nowrap sm:gap-3">
                            <span className="text-2xl" aria-hidden>
                                {categoryIcons[category.category] ?? "🍹"}
                            </span>
                            <div className="flex min-w-0 flex-col">
                                <h3 className="break-keep font-semibold uppercase tracking-wide text-primary dark:text-primary">
                                    {category.category}
                                </h3>
                            </div>
                        </div>
                        <span className="whitespace-nowrap rounded-full bg-[#285780]/10 px-3 py-1 text-xs font-bold text-[#285780] dark:bg-[#152231] dark:text-[#74D2E7]">
                            {category.items.length} items
                        </span>
                    </div>
                    <div className="space-y-4">
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
                </div>
            ))}
        </div>
    );
}
