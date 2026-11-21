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
