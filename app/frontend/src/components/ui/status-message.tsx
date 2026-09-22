import "./status-message.css";
import { useTranslation } from "react-i18next";
import { memo } from "react";

export type ConnectionNotice = "idle" | "lost" | null;

type Properties = {
    isRecording: boolean;
    notice?: ConnectionNotice;
};

const NOTICE_KEYS: Record<Exclude<ConnectionNotice, null>, string> = {
    idle: "status.sessionEndedIdle",
    lost: "status.connectionLost"
};

export default memo(function StatusMessage({ isRecording, notice = null }: Properties) {
    const { t } = useTranslation();
    if (!isRecording) {
        return (
            <p className="text mb-4 mt-6 text-sm text-muted-foreground" aria-live="polite">
                {t(notice ? NOTICE_KEYS[notice] : "status.notRecordingMessage")}
            </p>
        );
    }

    return (
        <div className="flex items-center" aria-live="polite">
            <div className="listening-equalizer">
                {[...Array(4)].map((_, index) => (
                    <span key={index} className={`bar bar-${(index % 3) + 1}`} />
                ))}
            </div>
            <p className="mb-4 ml-2 mt-6 font-semibold text-primary">
                {t("status.conversationInProgress")}
            </p>
        </div>
    );
});
