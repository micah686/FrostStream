const readinessForm = document.querySelector("[data-readiness]");
readinessForm?.addEventListener("submit", () => {
    for (const status of document.querySelectorAll(".check-state")) {
        status.textContent = "Running";
    }
    for (const symbol of document.querySelectorAll(".check-symbol")) {
        symbol.className = "check-symbol running";
        symbol.textContent = "↻";
    }
    readinessForm.querySelector("button").disabled = true;
});

const timeFormat = new Intl.DateTimeFormat(undefined, {
    year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
    second: "2-digit", timeZoneName: "shortOffset"
});
for (const time of document.querySelectorAll("[data-local-time]")) {
    const instant = new Date(time.dateTime);
    if (!Number.isNaN(instant.getTime())) {
        time.textContent = timeFormat.format(instant);
    }
}

if (document.querySelector("[data-refresh-job]")) {
    window.setInterval(() => {
        if (document.visibilityState === "visible" && document.activeElement === document.body &&
            !window.getSelection()?.toString()) {
            window.location.reload();
        }
    }, 5000);
}

// Refresh only evidence and the restore button, preserving any confirmation fields in progress.
if (document.querySelector("#verification-evidence")) {
    window.setInterval(async () => {
        const evidence = document.querySelector("#verification-evidence");
        if (evidence?.dataset.evidenceActive !== "true" || document.visibilityState !== "visible") return;
        try {
            const response = await fetch(window.location.href, { cache: "no-store" });
            if (!response.ok || response.redirected) return;
            const page = new DOMParser().parseFromString(await response.text(), "text/html");
            const updated = page.querySelector("#verification-evidence");
            const button = page.querySelector("#restore-button");
            if (updated) evidence.replaceWith(updated);
            if (button && document.querySelector("#restore-button")) document.querySelector("#restore-button").disabled = button.disabled;
        } catch { /* The next poll can retry. */ }
    }, 5000);
}
