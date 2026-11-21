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
    second: "2-digit", timeZone: "UTC", timeZoneName: "short"
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
            if (button && document.querySelector("#restore-button")) {
                const current = document.querySelector("#restore-button");
                current.dataset.serverBlocked = String(button.disabled);
                updateConfirmation(current.form);
            }
        } catch { /* The next poll can retry. */ }
    }, 5000);
}

for (const radio of document.querySelectorAll('.choice input[type="radio"]')) {
    radio.addEventListener('change', () => {
        for (const choice of document.querySelectorAll('.choice')) choice.classList.toggle('selected', !!choice.querySelector('input:checked'));
    });
}

function updateConfirmation(form) {
    if (!form) return;
    const button = form.querySelector('button[type="submit"]');
    const confirmation = form.querySelector('[data-expected]');
    if (!button || !confirmation) return;
    button.dataset.serverBlocked ??= String(button.disabled);
    button.disabled = button.dataset.serverBlocked === "true" || confirmation.value !== confirmation.dataset.expected ||
        [...form.querySelectorAll('.ack input[type="checkbox"]')].some(input => !input.checked) ||
        [...form.querySelectorAll('input[required]')].some(input => !input.validity.valid);
}
for (const form of document.querySelectorAll('[data-confirmation]')) {
    form.addEventListener('input', () => updateConfirmation(form));
    form.addEventListener('change', () => updateConfirmation(form));
    updateConfirmation(form);
}
document.querySelector('.step.active')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
