import * as React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils";

const switchVariants = cva("relative inline-flex items-center cursor-pointer", {
    variants: {
        size: {
            default: "w-11 h-6",
            sm: "w-9 h-5",
            lg: "w-14 h-7"
        }
    },
    defaultVariants: {
        size: "default"
    }
});

export interface SwitchProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, "size">, VariantProps<typeof switchVariants> {
    checked: boolean;
    onCheckedChange: (checked: boolean) => void;
}

const thumbConfig = {
    sm: { size: "h-4 w-4", checked: "translate-x-4" },
    default: { size: "h-5 w-5", checked: "translate-x-5" },
    lg: { size: "h-6 w-6", checked: "translate-x-7" }
} as const;

const Switch = React.forwardRef<HTMLInputElement, SwitchProps>(({ id, checked, onCheckedChange, className, size, ...props }, ref) => {
    const thumb = thumbConfig[size ?? "default"];

    return (
        <label className={cn(switchVariants({ size, className }))}>
            <input
                type="checkbox"
                id={id}
                checked={checked}
                onChange={e => onCheckedChange(e.target.checked)}
                className="peer sr-only"
                ref={ref}
                {...props}
            />
            <div
                className={cn(
                    "h-full w-full rounded-full relative transition-colors duration-200 ease-in-out",
                    checked ? "bg-primary" : "bg-input",
                    "peer-focus-visible:ring-2 peer-focus-visible:ring-ring peer-focus-visible:ring-offset-2 peer-focus-visible:ring-offset-background"
                )}
            >
                <div
                    className={cn(
                        "absolute left-[2px] top-[2px] rounded-full bg-white shadow-sm transition-transform duration-200 ease-in-out",
                        thumb.size,
                        checked && thumb.checked
                    )}
                />
            </div>
        </label>
    );
});
Switch.displayName = "Switch";

export { Switch, switchVariants };
