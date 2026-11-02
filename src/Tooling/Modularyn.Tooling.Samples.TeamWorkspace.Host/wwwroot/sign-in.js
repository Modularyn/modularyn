"use strict";

const form = document.getElementById("sign-in");
const status = document.getElementById("status");
form.addEventListener("submit", async (event) => {
    event.preventDefault();
    const tenant = form.elements.namedItem("tenant").value;
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(tenant)) {
        status.textContent = "Enter the tenant identifier supplied by your administrator.";
        return;
    }
    const button = form.querySelector("button");
    button.disabled = true;
    status.textContent = "Signing in…";
    try {
        const antiforgery = await fetch("/identity/antiforgery", { credentials: "same-origin", cache: "no-store" });
        if (!antiforgery.ok) throw new Error("Sign-in unavailable");
        const { requestToken } = await antiforgery.json();
        const returnUrl = "/t/" + tenant + "/notes/new";
        const response = await fetch("/identity/sign-in", {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": requestToken },
            body: JSON.stringify({ userName: form.elements.namedItem("userName").value,
                password: form.elements.namedItem("password").value, returnUrl })
        });
        form.elements.namedItem("password").value = "";
        if (!response.ok) {
            status.textContent = "Sign-in failed. Check your credentials or try again later.";
            return;
        }
        window.location.assign(returnUrl);
    } catch {
        form.elements.namedItem("password").value = "";
        status.textContent = "Sign-in is unavailable. Try again later.";
    } finally {
        button.disabled = false;
    }
});
