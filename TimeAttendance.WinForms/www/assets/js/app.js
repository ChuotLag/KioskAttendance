// =========================================================
// TimeAttendance Web UI (offline) - Router + WebView2 Bridge
// =========================================================

// ===== WebView2 Bridge bootstrap (must be loaded before any invoke) =====
(function initBridge() {
    if (window.bridge && typeof window.bridge.invoke === "function") return;

    const pending = new Map();
    let seq = 0;

    function ensureWebView2() {
        return !!(window.chrome && window.chrome.webview);
    }

    window.bridge = {
        invoke(type, payload) {
            return new Promise((resolve, reject) => {
                if (!ensureWebView2()) {
                    reject(new Error("bridge not available: window.chrome.webview is missing (not running inside WebView2)"));
                    return;
                }

                const id = String(++seq);
                pending.set(id, { resolve, reject });

                // gửi message xuống C#
                window.chrome.webview.postMessage(JSON.stringify({ id, type, payload }));

                // timeout để khỏi treo
                setTimeout(() => {
                    if (pending.has(id)) {
                        pending.delete(id);
                        reject(new Error("bridge timeout"));
                    }
                }, 10000);
            });
        }
    };

    // nhận message từ C#
    if (ensureWebView2()) {
        window.chrome.webview.addEventListener("message", (e) => {
            let msg = e.data;

            // Có thể C# gửi json string hoặc object
            try {
                if (typeof msg === "string") msg = JSON.parse(msg);
            } catch { /* ignore */ }

            const { id, ok, message, data } = msg || {};
            const p = pending.get(String(id));
            if (!p) return;

            pending.delete(String(id));

            if (ok) p.resolve({ ok, message, data });
            else p.reject(new Error(message || "Request failed"));
        });
    }
})();

// ------------------------------
(function () {
    "use strict";

    // ------------------------------
    // 1) WebView2 Bridge (JS <-> C#)
    const pending = new Map(); // id -> { resolve, reject, timer }

    function makeId() {
        // crypto.randomUUID is available on modern WebView2, but keep a fallback
        if (globalThis.crypto && typeof globalThis.crypto.randomUUID === "function") {
            return globalThis.crypto.randomUUID();
        }
        return (
            "id_" +
            Date.now().toString(16) +
            "_" +
            Math.random().toString(16).slice(2) +
            "_" +
            Math.random().toString(16).slice(2)
        );
    }

    function parseMaybeJson(x) {
        if (x == null) return null;
        if (typeof x === "string") {
            try {
                return JSON.parse(x);
            } catch {
                return null;
            }
        }
        if (typeof x === "object") return x;
        return null;
    }

    function onHostMessage(ev) {
        const msg = parseMaybeJson(ev?.data);
        if (!msg || !msg.id) return;

        const h = pending.get(msg.id);
        if (!h) return;

        pending.delete(msg.id);
        clearTimeout(h.timer);

        if (msg.ok) {
            h.resolve(msg);
        } else {
            const err = new Error(msg.message || "Request failed");
            err.code = msg.errorCode ?? null;
            err.data = msg.data ?? null;
            h.reject(err);
        }
    }

    function canUseBridge() {
        return !!(globalThis.chrome && chrome.webview && typeof chrome.webview.postMessage === "function");
    }

    if (canUseBridge()) {
        chrome.webview.addEventListener("message", onHostMessage);
    }

    globalThis.bridge = {
        /**
         * Invoke a C# handler by type with payload.
         * @param {string} type
         * @param {any} payload
         * @param {number} timeoutMs
         * @returns {Promise<{id:string, ok:boolean, message:string, data:any, errorCode?:number}>}
         */
        invoke(type, payload = {}, timeoutMs = 10000) {
            if (!canUseBridge()) {
                return Promise.reject(new Error("WebView2 bridge is not available. (chrome.webview missing)"));
            }

            if (payload === null || payload === undefined) payload = {};
            const id = makeId();
            const req = { id, type, payload };

            return new Promise((resolve, reject) => {
                const timer = setTimeout(() => {
                    pending.delete(id);
                    reject(new Error(`Timeout calling '${type}'.`));
                }, timeoutMs);

                pending.set(id, { resolve, reject, timer });

                try {
                    chrome.webview.postMessage(req); // WebView2 serializes object to JSON
                } catch (e) {
                    pending.delete(id);
                    clearTimeout(timer);
                    reject(e);
                }
            });
        },
    };

    // ------------------------------
    // 2) Router (load pages into #app-content)
    // ------------------------------
    const contentEl = document.getElementById("app-content");
    const titleEl = document.getElementById("page-title");

    // ------------------------------
    // 2.5) Admin PIN gate (single PIN for protected modules)
    // Allow without PIN: Dashboard, Chấm công QR, and Đổi mã PIN page
    const PIN_ALLOW_PAGES = new Set([
        "pages/dashboard.html",
        "pages/attendance_qr.html",
        "pages/pin_settings.html",
    ]);

    // Session cache (avoid asking repeatedly)
    let _pinOkUntil = 0; // epoch ms
    const PIN_TTL_MS = 2 * 60 * 1000; // 10 minutes

    function pageNeedsPin(page) {
        const p = String(page || "");
        if (!p.startsWith("pages/")) return false;
        return !PIN_ALLOW_PAGES.has(p);
    }

    function isPinSessionValid() {
        return Date.now() < (_pinOkUntil || 0);
    }

    function setPinSessionOk() {
        _pinOkUntil = Date.now() + PIN_TTL_MS;
    }

    /*function showPinModal() {
        return new Promise((resolve) => {
            const overlay = document.createElement("div");
            overlay.className = "fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4";
            overlay.innerHTML = `
              <div class="w-full max-w-md rounded-2xl bg-white border border-[#cfe5e7] shadow-xl">
                <div class="p-6">
                  <div class="flex items-center gap-2">
                    <span class="material-symbols-outlined text-[#4c939a]">lock</span>
                    <h3 class="text-lg font-bold">Nhập mã PIN</h3>
                  </div>
                  <p class="text-sm text-[#4c939a] mt-2">Mục này cần mã PIN của chủ quán.</p>

                  <div class="mt-4">
                    <input id="pin-modal-input" type="password" autocomplete="off"
                      class="w-full px-4 py-3 rounded-xl border border-[#cfe5e7] focus:outline-none focus:ring-2 focus:ring-primary/30"
                      placeholder="Nhập PIN" />
                    <div id="pin-modal-err" class="hidden mt-2 text-sm text-red-700"></div>
                  </div>

                  <div class="mt-5 flex items-center justify-end gap-3">
                    <button id="pin-modal-cancel" type="button"
                      class="h-10 px-4 rounded-xl border border-[#cfe5e7] bg-white font-semibold text-sm hover:bg-[#f5fbfc]">Hủy</button>
                    <button id="pin-modal-ok" type="button"
                      class="h-10 px-4 rounded-xl bg-primary text-[#0d1a1b] font-bold text-sm transition-transform active:scale-95">Xác nhận</button>
                  </div>
                </div>
              </div>`;

            document.body.appendChild(overlay);
            const input = overlay.querySelector("#pin-modal-input");
            const errEl = overlay.querySelector("#pin-modal-err");
            const btnOk = overlay.querySelector("#pin-modal-ok");
            const btnCancel = overlay.querySelector("#pin-modal-cancel");

            function close(val) {
                overlay.remove();
                resolve(val);
            }

            function showErr(msg) {
                if (!errEl) return;
                errEl.classList.remove("hidden");
                errEl.textContent = msg || "PIN không đúng";
            }

            async function doOk() {
                const pin = String(input?.value || "").trim();
                if (!pin) return showErr("Vui lòng nhập PIN.");

                try {
                    const res = await globalThis.bridge.invoke("ADMIN_PIN_VERIFY", { pin });
                    if (res?.data?.ok) {
                        setPinSessionOk();
                        close(true);
                    } else {
                        showErr("PIN không đúng.");
                    }
                } catch (e) {
                    showErr(e?.message || "Không thể kiểm tra PIN.");
                }
            }

            btnOk?.addEventListener("click", doOk);
            btnCancel?.addEventListener("click", () => close(false));
            overlay.addEventListener("click", (e) => {
                if (e.target === overlay) close(false);
            });
            input?.addEventListener("keydown", (e) => {
                if (e.key === "Enter") doOk();
                if (e.key === "Escape") close(false);
            });
            setTimeout(() => input?.focus(), 0);
        });
    }*/
    async function showPinModal() {
        // dùng modal có sẵn trong index.html
        document.body.classList.add("overflow-hidden");

        const pin = await openPinModal({
            subtitle: "Mục này cần mã PIN của chủ quán.",
            onConfirm: async (pin) => {
                const res = await globalThis.bridge.invoke("ADMIN_PIN_VERIFY", { pin });
                if (res?.data?.ok) {
                    setPinSessionOk();
                    return true; // ✅ đúng PIN -> đóng modal
                }
                return false; // ❌ sai PIN -> báo lỗi trong modal
            },
            onCancel: () => { /* optional */ }
        });

        document.body.classList.remove("overflow-hidden");
        return !!pin; // true nếu user nhập đúng, false nếu hủy
    }


    async function ensurePinIfNeeded(page) {
        if (!pageNeedsPin(page)) return true;
        if (isPinSessionValid()) return true;
        return await showPinModal();
    }

    function setTitle(text) {
        if (titleEl) titleEl.textContent = text || "";
    }

    function setActive(navEl) {
        document.querySelectorAll(".nav-item[data-page]").forEach((el) => {
            el.classList.remove("bg-primary", "text-[#0d1a1b]");
            el.classList.add("hover:bg-[#e7f2f3]", "dark:hover:bg-[#1a3538]");
        });

        navEl.classList.add("bg-primary", "text-[#0d1a1b]");
        navEl.classList.remove("hover:bg-[#e7f2f3]", "dark:hover:bg-[#1a3538]");
    }

    async function loadPage(page) {
        const res = await fetch(page, { cache: "no-store" });
        if (!res.ok) throw new Error(`Cannot load: ${page} (${res.status})`);
        if (!contentEl) throw new Error("Missing #app-content");
        contentEl.innerHTML = await res.text();
        try {
            const pageName = (page || "")
                .toLowerCase()
                .replace(/^pages\//, "")
                .replace(/\.html$/, "");

            const init = window.pageInit?.[pageName];
            if (typeof init === "function") init();
        } catch (e) {
            console.error("page init error", e);
        }
    }

    function escapeHtml(s) {
        return String(s ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");
    }

    // ------------------------------
    // 3) Page init: Dashboard
    // ------------------------------
    function setText(id, value) {
        const el = document.getElementById(id);
        if (el) el.textContent = String(value ?? "");
    }

    async function initDashboard() {
        if (!contentEl) return;
        if (!document.getElementById("kpi-checkedin")) return; // not loaded

        if (!globalThis.bridge?.invoke) {
            contentEl.insertAdjacentHTML(
                "afterbegin",
                `<div class="mb-4 p-4 rounded-xl border border-red-200 bg-red-50 text-red-700">
               <div class="font-bold">Thiếu bridge.invoke</div>
               <div class="text-sm mt-1">Kiểm tra WebView2 message bridge.</div>
             </div>`
            );
            return;
        }

        try {
            const kpiRes = await globalThis.bridge.invoke("DASHBOARD_GET", null, 12000);
            const kpi = kpiRes.data || {};
            setText("kpi-checkedin", kpi.checkedIn ?? 0);
            setText("kpi-working", kpi.working ?? 0);
            setText("kpi-notcheckedin", kpi.notCheckedIn ?? 0);
            const hours = (kpi.totalMinutes ?? 0) / 60;
            setText("kpi-totalhours", `${hours.toFixed(1)}h`);

            const actRes = await globalThis.bridge.invoke("RECENT_ACTIVITY_GET", null, 12000);
            const rows = Array.isArray(actRes.data) ? actRes.data : [];

            const tbody = document.getElementById("recent-activity-body");
            if (!tbody) return;

            const initials = (name) =>
                String(name || "?")
                    .trim()
                    .split(/\s+/)
                    .slice(0, 2)
                    .map((s) => (s[0] || "").toUpperCase())
                    .join("");

            const typeMeta = (t) =>
                t === 1
                    ? { label: "Check-in", icon: "login", badge: "bg-[#e7f2f3] dark:bg-[#1a3538]" }
                    : t === 2
                        ? { label: "Check-out", icon: "logout", badge: "bg-gray-100 dark:bg-[#1a3538]" }
                        : { label: "Activity", icon: "info", badge: "bg-gray-100 dark:bg-[#1a3538]" };

            tbody.innerHTML = rows
                .map((r) => {
                    const m = typeMeta(r.eventType);
                    const dt = new Date(r.eventTime);
                    return `
                        <tr class="hover:bg-gray-50 dark:hover:bg-[#1a3538] transition-colors">
                          <td class="px-6 py-4 whitespace-nowrap">
                            <div class="flex items-center gap-3">
                              <div class="size-8 rounded-full bg-gray-200 dark:bg-gray-700 flex items-center justify-center text-xs font-bold">
                                ${escapeHtml(initials(r.fullName))}
                              </div>
                              <p class="text-sm font-semibold dark:text-white">${escapeHtml(r.fullName)}</p>
                            </div>
                          </td>
                          <td class="px-6 py-4 whitespace-nowrap">
                            <span class="inline-flex items-center gap-1 px-2.5 py-1 rounded-lg ${m.badge} text-[#0d1a1b] dark:text-white text-[11px] font-bold">
                              <span class="material-symbols-outlined text-[14px]">${m.icon}</span> ${m.label}
                            </span>
                          </td>
                          <td class="px-6 py-4 whitespace-nowrap text-sm text-[#4c939a]">${escapeHtml(dt.toLocaleString())}</td>
                          <td class="px-6 py-4 whitespace-nowrap text-sm text-[#4c939a]">${escapeHtml(r.deviceCode)}</td>
                          <td class="px-6 py-4 whitespace-nowrap">
                            <span class="inline-flex items-center gap-1 px-2.5 py-1 rounded-lg bg-green-100 dark:bg-green-900/30 text-green-700 dark:text-green-400 text-[11px] font-bold">
                              <span class="material-symbols-outlined text-[14px]">check_circle</span> Success
                            </span>
                          </td>
                        </tr>`;
                })
                .join("");
        } catch (err) {
            console.error(err);
            contentEl.insertAdjacentHTML(
                "afterbegin",
                `<div class="mb-4 p-4 rounded-xl border border-red-200 bg-red-50 text-red-700">
                   <div class="font-bold">Không thể tải dữ liệu Dashboard</div>
                   <div class="text-sm mt-1">${escapeHtml(err?.message || "Unknown error")}</div>
                 </div>`
            );
        }
    }

    // ------------------------------
    // 4) Page init: Attendance
    // ------------------------------
    function setAttendanceStatus(ok, message) {
        const el = document.getElementById("att-status");
        if (!el) return;

        el.classList.remove("hidden");
        el.className =
            "w-full max-w-[360px] mt-3 px-4 py-3 rounded-lg text-sm font-semibold " +
            (ok
                ? "bg-green-50 text-green-700 dark:bg-green-900/30 dark:text-green-300"
                : "bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-300");
        el.textContent = message || "";
    }

    function getPin() {
        return Array.from(document.querySelectorAll("[data-pin]"))
            .map((i) => String(i.value || "").trim())
            .join("");
    }

    async function initAttendance() {
        if (!document.getElementById("btn-checkin")) return; // not loaded

        const pinInputs = Array.from(document.querySelectorAll("[data-pin]"));
        pinInputs.forEach((inp, idx) => {
            inp.addEventListener("input", () => {
                inp.value = String(inp.value || "").replace(/\D/g, "").slice(-1);
                if (inp.value && pinInputs[idx + 1]) pinInputs[idx + 1].focus();
            });

            inp.addEventListener("keydown", (e) => {
                if (e.key === "Backspace" && !inp.value && pinInputs[idx - 1]) {
                    pinInputs[idx - 1].focus();
                }
                if (e.key === "Enter") {
                    document.getElementById("btn-checkin")?.click();
                }
            });
        });

        const doAction = async (type) => {
            const employeeCode = String(document.getElementById("employee-code")?.value || "").trim();
            const pin = getPin();

            if (!employeeCode) return setAttendanceStatus(false, "Vui lòng nhập EmployeeCode (test).");
            if (pin.length !== 4) return setAttendanceStatus(false, "Vui lòng nhập đủ 4 số PIN.");
            if (!globalThis.bridge?.invoke) return setAttendanceStatus(false, "Thiếu bridge.invoke (assets/js/app.js).");

            try {
                const res = await globalThis.bridge.invoke(type, { employeeCode, pin }, 12000);
                setAttendanceStatus(true, res.message || "OK");
            } catch (err) {
                setAttendanceStatus(false, err?.message || "Lỗi");
            }
        };

        document.getElementById("btn-checkin")?.addEventListener("click", () => doAction("CHECK_IN"));
        document.getElementById("btn-checkout")?.addEventListener("click", () => doAction("CHECK_OUT"));
    }

    async function runPageInit(page) {
        const p = String(page || "");
        if (p.includes("dashboard.html")) await initDashboard();
        // Các trang khác đã tự gọi pageInit theo tên file trong loadPage()
    }

    async function navigate(navEl) {
        const page = navEl.dataset.page;
        const title = navEl.dataset.title || "Page";

        // Admin PIN gate
        /*   if (pageNeedsPin(page) && !isPinSessionValid()) {
               const ok = await showPinModal();
               if (!ok) return;
           }*/
        const ok = await ensurePinIfNeeded(page);
        if (!ok) return;


        try {
            await loadPage(page);
            setActive(navEl);
            setTitle(title);
            await runPageInit(page);
        } catch (err) {
            console.error(err);
            if (contentEl) {
                contentEl.innerHTML = `
          <div class="p-6 rounded-xl border border-red-200 bg-red-50 text-red-700">
            <div class="font-bold mb-1">Không thể tải trang</div>
            <div class="text-sm">${escapeHtml(page)}</div>
            <div class="text-sm mt-2">${escapeHtml(err?.message || "Unknown error")}</div>
          </div>`;
            }
        }
    }

    // Click handler (sidebar)
    document.addEventListener("click", (e) => {
        const navEl = e.target.closest(".nav-item[data-page]");
        if (!navEl) return;
        e.preventDefault?.();
        navigate(navEl);
    });

    // Default page
    document.addEventListener("DOMContentLoaded", () => {
        const defaultEl =
            document.querySelector('.nav-item[data-page="pages/dashboard.html"]') ||
            document.querySelector(".nav-item[data-page]");
        if (defaultEl) navigate(defaultEl);
    });
})();

// ===== Attendance page init =====
window.pageInit = window.pageInit || {};

window.pageInit.attendance = function () {
    const empInput = document.getElementById("att-emp");
    const empList = document.getElementById("att-emp-list");
    const empName = document.getElementById("att-emp-name");

    const btnIn = document.getElementById("btn-checkin");
    const btnOut = document.getElementById("btn-checkout");
    const statusBox = document.getElementById("att-status");

    // (Optional) QR block - chỉ có ở một số layout cũ.
    // Trang nhập PIN riêng (pages/attendance.html) KHÔNG có QR.
    // Trang QR riêng (pages/attendance_qr.html) dùng pageInit.attendance_qr.
    const img = document.getElementById("qr-img");
    const link = document.getElementById("qr-link");
    const left = document.getElementById("qr-left");
    const btnRefresh = document.getElementById("qr-refresh");

    if (!empInput || !btnIn || !btnOut) return;

    const isWebView2 = !!(window.chrome && window.chrome.webview);
    if (!isWebView2) {
        // Trang này thiết kế để chạy trong WinForms WebView2
        if (statusBox) {
            statusBox.classList.remove("hidden");
            statusBox.className = "mt-5 px-4 py-3 rounded-lg text-sm font-semibold bg-amber-50 text-amber-700 border border-amber-200";
            statusBox.textContent = "Trang Attendance này cần chạy bên trong ứng dụng WinForms (WebView2).";
        }
        return;
    }

    function showStatus(type, msg) {
        if (!statusBox) return;
        statusBox.classList.remove("hidden");
        // type: "ok" | "err" | "info"
        statusBox.className =
            "mt-5 px-4 py-3 rounded-lg text-sm font-semibold border " +
            (type === "ok"
                ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                : type === "err"
                    ? "bg-red-50 text-red-700 border-red-200"
                    : "bg-slate-50 text-slate-700 border-slate-200");
        statusBox.textContent = msg;
    }

    // ===== QR block (optional) =====
    // Nếu trang có QR thì hỗ trợ load/refresh. Nếu không có QR (attendance.html) thì bỏ qua.
    if (img) {
        // Tránh chạy nhiều timer khi chuyển trang qua lại
        if (globalThis.__attQrTimer) {
            clearInterval(globalThis.__attQrTimer);
            globalThis.__attQrTimer = null;
        }

        async function loadQr() {
            try {
                // gọi xuống C# => trả { url, expiresInSeconds, qrPngBase64 }
                const res = await window.bridge.invoke("KIOSK_QR_GET");
                const d = res.data || {};

                // render QR
                if (d.qrPngBase64) img.src = `data:image/png;base64,${d.qrPngBase64}`;
                else img.removeAttribute("src");

                if (link) link.value = d.url || "";

                // countdown
                let sec = Number(d.expiresInSeconds ?? 30);
                if (!Number.isFinite(sec) || sec <= 0) sec = 30;
                if (left) left.textContent = String(sec);

                if (globalThis.__attQrTimer) clearInterval(globalThis.__attQrTimer);
                globalThis.__attQrTimer = setInterval(() => {
                    sec--;
                    if (left) left.textContent = String(Math.max(sec, 0));
                    if (sec <= 0) {
                        clearInterval(globalThis.__attQrTimer);
                        globalThis.__attQrTimer = null;
                        loadQr(); // auto refresh khi hết hạn
                    }
                }, 1000);
            } catch (e) {
                console.error("KIOSK_QR_GET error", e);
                if (left) left.textContent = "--";
                if (link) link.value = "";
                img.removeAttribute("src");
                showStatus("err", "Không tải được QR. Kiểm tra API/Bridge (xem console).");
            }
        }

        btnRefresh?.addEventListener("click", (ev) => {
            ev.preventDefault();
            loadQr();
        });

        // Nếu trang có QR thì tự load 1 lần
        loadQr();
    }


    // ===== PIN UX (4 ô data-pin) =====
    const pinInputs = Array.from(document.querySelectorAll("input[data-pin]"));

    pinInputs.forEach((el, idx) => {
        el.setAttribute("autocomplete", "off");
        el.addEventListener("input", () => {
            el.value = (el.value || "").replace(/\D/g, "").slice(0, 1);
            if (el.value && idx < pinInputs.length - 1) pinInputs[idx + 1].focus();
        });

        el.addEventListener("keydown", (evt) => {
            if (evt.key === "Backspace" && !el.value && idx > 0) {
                pinInputs[idx - 1].focus();
                return;
            }
            if (evt.key === "Enter") {
                evt.preventDefault();
                doAction("CHECK_IN");
            }
        });
    });

    function getPin() {
        return pinInputs.map(i => (i.value || "").trim()).join("");
    }

    function clearPin() {
        pinInputs.forEach(i => (i.value = ""));
        pinInputs[0]?.focus();
    }

    // ===== Employees =====
    const empMap = new Map(); // code -> fullName
    const empNameToCode = new Map(); // fullnameLower -> code

    function renderEmployeeName() {
        const v = (empInput.value || "").trim();
        let name = "";

        if (empMap.has(v)) {
            name = empMap.get(v) || "";
        } else if (v) {
            const code = empNameToCode.get(v.toLowerCase());
            if (code) name = empMap.get(code) || v;
            else name = v; // người dùng tự gõ tên
        }

        if (empName) empName.textContent = name ? name : "";
    }

    empInput.addEventListener("input", () => {
        renderEmployeeName();
    });

    async function loadEmployees() {
        try {
            showStatus("info", "Đang tải danh sách nhân viên...");
            const res = await window.bridge.invoke("EMP_LIST", { includeInactive: false });
            const rows = Array.isArray(res.data) ? res.data : [];

            if (empList) empList.innerHTML = "";
            empMap.clear();
            empNameToCode.clear();

            for (const e of rows) {
                const code = (e.employeeCode || "").trim();
                const fullName = (e.fullName || "").trim();
                if (!code) continue;

                empMap.set(code, fullName);

                if (fullName) {
                    // map tên -> mã (để người dùng chọn bằng tên)
                    empNameToCode.set(fullName.toLowerCase(), code);
                }

                if (empList) {
                    const opt = document.createElement("option");
                    // ✅ Hiển thị trong danh sách: chỉ tên
                    opt.value = fullName || code;
                    opt.label = fullName || "";
                    empList.appendChild(opt);
                }
            }

            showStatus("info", "Sẵn sàng điểm danh.");

            // focus
            empInput.focus();
            empInput.select();
            clearPin();
        } catch (e) {
            console.error("EMP_LIST", e);
            showStatus("err", e?.message || "Không tải được danh sách nhân viên.");
        }
    }

    async function doAction(type) {
        const raw = (empInput.value || "").trim();
        let employeeCode = raw;

        // ✅ Nếu user chọn theo TÊN thì đổi về MÃ để gửi API
        if (raw && !empMap.has(raw)) {
            const code = empNameToCode.get(raw.toLowerCase());
            if (code) employeeCode = code;
        }
        const pin = getPin();

        if (!employeeCode) return showStatus("err", "Vui lòng chọn nhân viên.");
        if (!empMap.has(employeeCode)) return showStatus("err", "Nhân viên không hợp lệ. Vui lòng chọn lại.");
        if (!pin || pin.length !== 4) return showStatus("err", "Vui lòng nhập đúng 4 số PIN.");

        try {
            const res = await window.bridge.invoke(type, { employeeCode, pin });
            showStatus("ok", res.message || "Thành công.");
            renderEmployeeName();
            clearPin();
        } catch (e) {
            console.error(type, e);
            showStatus("err", e?.message || "Thao tác thất bại.");
            // giữ mã nhân viên, chỉ select lại pin
            pinInputs[0]?.focus();
            pinInputs.forEach(i => i.select?.());
        }
    }

    btnIn.addEventListener("click", (e) => { e.preventDefault(); doAction("CHECK_IN"); });
    btnOut.addEventListener("click", (e) => { e.preventDefault(); doAction("CHECK_OUT"); });

    // Load page
    loadEmployees();
};

// ===== Attendance QR-only (kiosk screen) =====
// Trang: pages/attendance_qr.html
// Chỉ cần hiển thị QR (đổi mỗi 30s) để nhân viên quét bằng điện thoại.
window.pageInit.attendance_qr = function () {
    const img = document.getElementById("qr-img");
    const left = document.getElementById("qr-left");
    const btnRefresh = document.getElementById("qr-refresh");
    const statusBox = document.getElementById("qr-status");
    const link = document.getElementById("qr-link");

    // Không có QR block thì thôi
    if (!img) return;

    const isWebView2 = !!(window.chrome && chrome.webview);
    if (!isWebView2) {
        if (statusBox) {
            statusBox.classList.remove("hidden");
            statusBox.className = "mt-4 px-4 py-3 rounded-lg text-sm font-semibold bg-amber-50 text-amber-700 border border-amber-200";
            statusBox.textContent = "Trang này cần chạy trong ứng dụng WinForms (WebView2) để lấy QR.";
        }
        if (left) left.textContent = "--";
        return;
    }

    // Tránh chạy nhiều timer khi chuyển trang
    if (globalThis.__kioskQrTimer) {
        clearInterval(globalThis.__kioskQrTimer);
        globalThis.__kioskQrTimer = null;
    }

    function showStatus(type, msg) {
        if (!statusBox) return;
        statusBox.classList.remove("hidden");
        statusBox.className =
            "mt-4 px-4 py-3 rounded-lg text-sm font-semibold border " +
            (type === "ok"
                ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                : type === "err"
                    ? "bg-red-50 text-red-700 border-red-200"
                    : "bg-slate-50 text-slate-700 border-slate-200");
        statusBox.textContent = msg;
    }

    async function loadQr() {
        try {
            const res = await window.bridge.invoke("KIOSK_QR_GET");
            const d = res.data || {};

            if (d.qrPngBase64) {
                img.src = `data:image/png;base64,${d.qrPngBase64}`;
            } else {
                img.removeAttribute("src");
            }

            if (link) link.value = d.url || "";

            let sec = Number(d.expiresInSeconds ?? 30);
            if (!Number.isFinite(sec) || sec <= 0) sec = 30;
            if (left) left.textContent = String(sec);

            if (globalThis.__kioskQrTimer) clearInterval(globalThis.__kioskQrTimer);
            globalThis.__kioskQrTimer = setInterval(() => {
                sec--;
                if (left) left.textContent = String(Math.max(sec, 0));
                if (sec <= 0) {
                    clearInterval(globalThis.__kioskQrTimer);
                    globalThis.__kioskQrTimer = null;
                    loadQr();
                }
            }, 1000);

            // Ẩn status nếu load OK
            if (statusBox) statusBox.classList.add("hidden");
        } catch (e) {
            console.error("KIOSK_QR_GET error", e);
            if (left) left.textContent = "--";
            img.removeAttribute("src");
            showStatus("err", e?.message || "Không tải được QR. Kiểm tra API/Bridge (xem console).");
        }
    }

    btnRefresh?.addEventListener("click", (ev) => {
        ev.preventDefault();
        loadQr();
    });

    loadQr();
};

// ===== Employees page init (CRUD) =====
window.pageInit.employees = function () {
    const tbody = document.getElementById("emp-tbody");
    if (!tbody) return;

    // List controls
    const txtSearch = document.getElementById("emp-search");
    const chkIncludeInactive = document.getElementById("emp-include-inactive");
    const btnAdd = document.getElementById("btn-emp-add");
    const statusBox = document.getElementById("emp-status");

    // Modal controls
    const modal = document.getElementById("emp-modal");
    const btnClose = document.getElementById("btn-emp-close");
    const btnCancel = document.getElementById("btn-emp-cancel");
    const btnSave = document.getElementById("btn-emp-save");
    const modalTitle = document.getElementById("emp-modal-title");
    const modalErr = document.getElementById("emp-modal-error");
    const hidId = document.getElementById("emp-id");
    const inpCode = document.getElementById("emp-code"); // giờ chỉ hiển thị, không cho nhập
    const inpName = document.getElementById("emp-name");
    const inpPhone = document.getElementById("emp-phone");
    const inpRate = document.getElementById("emp-rate");
    const inpPin = document.getElementById("emp-pin");
    const chkActive = document.getElementById("emp-active");

    let all = [];

    // Nếu có ô mã thì set readonly (vì mã tự sinh)
    if (inpCode) {
        inpCode.readOnly = true;
        inpCode.placeholder = "Tự động tạo";
    }

    // ---------- helpers ----------
    const escapeHtml = (s) => String(s ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");

    function parseMoney(v) {
        // hỗ trợ "50.000" / "50,000" / "50000"
        const s = String(v ?? "").trim();
        if (!s) return NaN;
        const cleaned = s.replaceAll(" ", "").replaceAll(".", "").replaceAll(",", ".");
        const n = Number(cleaned);
        return Number.isFinite(n) ? n : NaN;
    }

    function setStatus(type, msg) {
        if (!statusBox) return;
        if (!msg) {
            statusBox.classList.add("hidden");
            statusBox.textContent = "";
            return;
        }
        statusBox.classList.remove("hidden");
        statusBox.className =
            "mt-4 p-4 rounded-xl border text-sm font-semibold " +
            (type === "ok" ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                : type === "err" ? "bg-red-50 text-red-700 border-red-200"
                    : "bg-slate-50 text-slate-700 border-slate-200");
        statusBox.textContent = msg;
    }

    function openModal(mode, emp) {
        if (!modal) return;

        // reset
        if (modalErr) {
            modalErr.style.display = "none";
            modalErr.textContent = "";
        }

        if (mode === "create") {
            modalTitle && (modalTitle.textContent = "Thêm nhân viên");
            hidId.value = "";

            // mã tự động tạo
            if (inpCode) inpCode.value = "Tự động tạo";

            inpName.value = "";
            inpPhone.value = "";
            inpRate.value = "";
            inpPin.value = "";
            chkActive.checked = true;
        } else {
            modalTitle && (modalTitle.textContent = "Cập nhật nhân viên");
            hidId.value = String(emp.employeeId);

            if (inpCode) inpCode.value = emp.employeeCode ?? "";

            inpName.value = emp.fullName ?? "";
            inpPhone.value = emp.phone ?? "";
            inpRate.value = String(emp.hourlyRate ?? 0);
            inpPin.value = ""; // không hiển thị PIN cũ
            chkActive.checked = !!emp.isActive;
        }

        modal.classList.add("is-open");
        modal.setAttribute("aria-hidden", "false");
        setTimeout(() => inpName?.focus(), 50); // focus vào tên (không focus mã)
    }

    function closeModal() {
        if (!modal) return;
        modal.classList.remove("is-open");
        modal.setAttribute("aria-hidden", "true");
    }

    function showModalError(msg) {
        if (!modalErr) return;
        modalErr.textContent = msg;
        modalErr.style.display = "block";
    }

    function normalizePin(pin) {
        const p = String(pin || "").trim();
        if (!p) return "";
        return p.replace(/\D/g, ""); // chỉ giữ số
    }

    function getFiltered() {
        const q = (txtSearch?.value || "").trim().toLowerCase();
        if (!q) return all;
        return all.filter(e => {
            const code = String(e.employeeCode || "").toLowerCase();
            const name = String(e.fullName || "").toLowerCase();
            const phone = String(e.phone || "").toLowerCase();
            return code.includes(q) || name.includes(q) || phone.includes(q);
        });
    }

    function render() {
        const rows = getFiltered();
        if (!rows || rows.length === 0) {
            tbody.innerHTML = `<tr><td colspan="6" class="px-6 py-4 text-[#4c939a]">Không có dữ liệu</td></tr>`;
            return;
        }

        tbody.innerHTML = rows.map(e => {
            const rate = Number(e.hourlyRate || 0);
            const badge = e.isActive
                ? `<span class="inline-flex items-center gap-1 px-2 py-1 rounded-lg text-xs font-bold bg-emerald-50 text-emerald-700 border border-emerald-200">Đang làm</span>`
                : `<span class="inline-flex items-center gap-1 px-2 py-1 rounded-lg text-xs font-bold bg-gray-100 text-gray-600 border border-gray-200">Đã nghỉ</span>`;

            const btnEdit = `<button data-action="edit" data-id="${e.employeeId}" class="h-8 px-3 rounded-lg border border-[#cfe5e7] dark:border-[#2a3c3e] text-xs font-bold hover:bg-[#e7f2f3] dark:hover:bg-[#1a3538]">Sửa</button>`;
            const btnSoft = `<button data-action="soft" data-id="${e.employeeId}" class="h-8 px-3 rounded-lg border border-red-200 text-xs font-bold text-red-700 hover:bg-red-50">Cho nghỉ</button>`;
            const btnRestore = `<button data-action="restore" data-id="${e.employeeId}" class="h-8 px-3 rounded-lg border border-emerald-200 text-xs font-bold text-emerald-700 hover:bg-emerald-50">Khôi phục</button>`;
            const btnHard = `<button data-action="hard" data-id="${e.employeeId}" class="h-8 px-3 rounded-lg border border-red-200 text-xs font-bold text-red-700 hover:bg-red-50">Xóa vĩnh viễn</button>`;

            const actions = e.isActive ? `${btnEdit} ${btnSoft}` : `${btnRestore} ${btnHard}`;

            return `
                <tr>
                    <td class="px-6 py-3 font-bold">${escapeHtml(e.employeeCode || "")}</td>
                    <td class="px-6 py-3">${escapeHtml(e.fullName || "")}</td>
                    <td class="px-6 py-3">${escapeHtml(e.phone || "")}</td>
                    <td class="px-6 py-3">${rate.toLocaleString("vi-VN")}</td>
                    <td class="px-6 py-3">${badge}</td>
                    <td class="px-6 py-3 text-right">
                        <div class="inline-flex gap-2">${actions}</div>
                    </td>
                </tr>
            `;
        }).join("");
    }

    async function load() {
        setStatus("info", "Đang tải danh sách nhân viên...");
        try {
            const includeInactive = !!chkIncludeInactive?.checked;
            const res = await window.bridge.invoke("EMPLOYEE_LIST", { includeInactive });

            // normalize key (PascalCase hoặc camelCase đều ok)
            const raw = Array.isArray(res.data) ? res.data : [];
            /*    all = raw.map(x => ({
                    employeeId: x.employeeId ?? x.EmployeeId,
                    employeeCode: x.employeeCode ?? x.EmployeeCode,
                    fullName: x.fullName ?? x.FullName,
                    phone: x.phone ?? x.Phone,
                    hourlyRate: x.hourlyRate ?? x.HourlyRate,
                    isActive: x.isActive ?? x.IsActive
                }));*/
            all = (Array.isArray(res.data) ? res.data : []).map(x => ({
                employeeId: x.employeeId ?? x.EmployeeId,
                employeeCode: x.employeeCode ?? x.EmployeeCode,
                fullName: x.fullName ?? x.FullName,
                phone: x.phone ?? x.Phone,
                hourlyRate: x.hourlyRate ?? x.HourlyRate,
                isActive: x.isActive ?? x.IsActive
            }));


            setStatus(null, "");
            render();
        } catch (e) {
            console.error("EMPLOYEE_LIST", e);
            setStatus("err", e?.message || "Không tải được danh sách nhân viên (xem console).");
            all = [];
            render();
        }
    }

    async function save() {
        // validate
        const fullName = String(inpName?.value || "").trim();
        const phone = String(inpPhone?.value || "").trim();
        const hourlyRate = parseMoney(inpRate?.value);
        const isActive = !!chkActive?.checked;
        const pinDigits = normalizePin(inpPin?.value);

        if (!fullName) return showModalError("Vui lòng nhập Họ tên.");
        if (!Number.isFinite(hourlyRate) || hourlyRate <= 0) return showModalError("Lương/giờ phải > 0.");
        if (pinDigits && (pinDigits.length < 4 || pinDigits.length > 6)) return showModalError("PIN phải từ 4 đến 6 số.");

        const isCreate = !String(hidId?.value || "").trim();

        // ✅ CREATE: KHÔNG gửi employeeCode nữa
        // ✅ UPDATE: dùng employeeId, KHÔNG gửi employeeCode
        const payload = isCreate
            ? { fullName, phone: phone || null, hourlyRate, pin: pinDigits || null }
            : {
                employeeId: Number(hidId.value),
                fullName,
                phone: phone || null,
                hourlyRate,
                isActive,
                pin: pinDigits || null
            };

        btnSave && (btnSave.disabled = true);
        try {
            await window.bridge.invoke(isCreate ? "EMPLOYEE_CREATE" : "EMPLOYEE_UPDATE", payload, 12000);
            closeModal();
            await load();
            setStatus("ok", isCreate ? "Đã thêm nhân viên." : "Đã cập nhật nhân viên.");
            setTimeout(() => setStatus(null, ""), 2500);
        } catch (e) {
            console.error(isCreate ? "EMPLOYEE_CREATE" : "EMPLOYEE_UPDATE", e);
            showModalError(e?.message || "Lưu thất bại (xem console).");
        } finally {
            btnSave && (btnSave.disabled = false);
        }
    }

    async function softDelete(employeeId) {
        if (!confirm("Cho nhân viên nghỉ? (Dữ liệu chấm công/lương vẫn được giữ)")) return;
        try {
            await window.bridge.invoke("EMPLOYEE_DELETE", { employeeId, hardDelete: false });
            await load();
        } catch (e) {
            alert(e?.message || "Thao tác thất bại.");
        }
    }

    async function hardDelete(employeeId) {
        if (!confirm("Xóa vĩnh viễn nhân viên này? (Không khôi phục được)")) return;
        try {
            await window.bridge.invoke("EMPLOYEE_DELETE", { employeeId, hardDelete: true });
            await load();
        } catch (e) {
            alert(e?.message || "Thao tác thất bại.");
        }
    }

    async function restore(emp) {
        try {
            // ✅ Restore bằng UPDATE theo employeeId (không gửi employeeCode)
            await window.bridge.invoke("EMPLOYEE_UPDATE", {
                employeeId: emp.employeeId,
                fullName: emp.fullName,
                phone: emp.phone || null,
                hourlyRate: Number(emp.hourlyRate || 0),
                isActive: true,
                pin: null
            });
            await load();
        } catch (e) {
            alert(e?.message || "Thao tác thất bại.");
        }
    }

    // ---------- events ----------
    btnAdd?.addEventListener("click", () => openModal("create"));
    btnClose?.addEventListener("click", closeModal);
    btnCancel?.addEventListener("click", closeModal);
    btnSave?.addEventListener("click", save);

    // backdrop click
    modal?.addEventListener("click", (ev) => {
        const t = ev.target;
        if (t?.dataset?.empClose === "1") closeModal();
    });

    // ESC
    document.addEventListener("keydown", (ev) => {
        if (ev.key === "Escape" && modal?.classList.contains("is-open")) closeModal();
    });

    txtSearch?.addEventListener("input", () => render());
    chkIncludeInactive?.addEventListener("change", () => load());

    tbody.addEventListener("click", (ev) => {
        const btn = ev.target.closest("button[data-action]");
        if (!btn) return;
        const action = btn.dataset.action;
        const id = Number(btn.dataset.id);
        const emp = all.find(x => Number(x.employeeId) === id);
        if (!emp) return;

        if (action === "edit") return openModal("edit", emp);
        if (action === "soft") return softDelete(id);
        if (action === "hard") return hardDelete(id);
        if (action === "restore") return restore(emp);
    });

    // load on enter
    load();
};

// ===== Scheduling page init (Next Week Calendar - multi shifts per day) =====
// Page file name should be: pages/scheduling.html  => pageInit key: 'scheduling'
// Supports: 1 nhân viên có thể làm nhiều ca trong 1 ngày (multiple assignments per WorkDate).
window.pageInit = window.pageInit || {};

window.pageInit.scheduling = function () {
    // Try to find existing controls (preferred)
    const root = document.getElementById('app-content') || document.body;

    const selEmp =
        document.getElementById('sch-emp') ||
        document.querySelector('select[data-sch="emp"]') ||
        root.querySelector('select');

    const btnReload =
        document.getElementById('sch-reload') ||
        document.querySelector('[data-sch="reload"]') ||
        Array.from(root.querySelectorAll('button')).find(b => (b.textContent || '').trim().toLowerCase() === 'tải lại');

    let weekLabel = document.getElementById('sch-week-label') || document.querySelector('[data-sch="week"]');
    let statusBox = document.getElementById('sch-status') || document.querySelector('[data-sch="status"]');

    // Ensure grid scaffolding exists even if scheduling.html missed the container
    let head = document.getElementById('sch-grid-head') || document.getElementById('sch-head');
    let body = document.getElementById('sch-grid-body') || document.getElementById('sch-body');

    function ensureScaffold() {
        // week label
        if (!weekLabel) {
            weekLabel = document.createElement('div');
            weekLabel.id = 'sch-week-label';
            weekLabel.className = 'text-sm text-slate-500 mt-1';
            const title = root.querySelector('h1, h2, .text-xl, .text-2xl, .text-3xl');
            if (title && title.parentElement) title.parentElement.appendChild(weekLabel);
            else root.prepend(weekLabel);
        }

        // status box
        if (!statusBox) {
            statusBox = document.createElement('div');
            statusBox.id = 'sch-status';
            statusBox.className = 'hidden mt-4 p-3 rounded-xl border text-sm font-semibold';
            const hr = root.querySelector('hr');
            if (hr && hr.parentElement) hr.parentElement.insertBefore(statusBox, hr.nextSibling);
            else root.appendChild(statusBox);
        }

        // grid container
        if (!head || !body) {
            const container = document.createElement('div');
            container.className = 'mt-6 border border-[#cfe5e7] rounded-2xl overflow-hidden bg-white';

            head = document.createElement('div');
            head.id = 'sch-grid-head';
            head.className = 'grid';
            head.style.gridTemplateColumns = 'repeat(7, 1fr)';

            body = document.createElement('div');
            body.id = 'sch-grid-body';
            body.className = 'grid';
            body.style.gridTemplateColumns = 'repeat(7, 1fr)';

            container.appendChild(head);
            container.appendChild(body);

            const headerWrap = root.querySelector('#sch-week-label')?.closest('div') || root.firstElementChild;
            if (headerWrap && headerWrap.parentElement) headerWrap.parentElement.appendChild(container);
            else root.appendChild(container);
        }
    }

    ensureScaffold();

    const escapeHtml = (s) => String(s ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');

    function setStatus(type, msg) {
        if (!statusBox) return;
        if (!msg) {
            statusBox.classList.add('hidden');
            statusBox.textContent = '';
            return;
        }
        statusBox.classList.remove('hidden');
        statusBox.className =
            'mt-4 p-3 rounded-xl border text-sm font-semibold ' +
            (type === 'ok'
                ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
                : type === 'err'
                    ? 'bg-red-50 text-red-700 border-red-200'
                    : 'bg-slate-50 text-slate-700 border-slate-200');
        statusBox.textContent = msg;
    }

    const pad2 = (n) => String(n).padStart(2, '0');
    const isoDate = (d) => `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
    const vnDate = (d) => `${pad2(d.getDate())}/${pad2(d.getMonth() + 1)}`;

    function addDays(d, n) {
        const x = new Date(d);
        x.setDate(x.getDate() + n);
        x.setHours(0, 0, 0, 0);
        return x;
    }

    // Monday of next week
    function getNextWeekStart() {
        const now = new Date();
        const day = now.getDay(); // 0=Sun
        const diffToMonThisWeek = (day === 0 ? -6 : 1 - day);
        const monThisWeek = addDays(now, diffToMonThisWeek);
        return addDays(monThisWeek, 7);
    }

    const weekStart = getNextWeekStart();
    const days = Array.from({ length: 7 }, (_, i) => addDays(weekStart, i));
    const dateFrom = isoDate(days[0]);
    const dateTo = isoDate(days[6]);

    if (weekLabel) weekLabel.textContent = `Tuần: ${vnDate(days[0])} - ${vnDate(days[6])}`;

    // State
    let shifts = [];
    // workDate -> Array<scheduleRow>
    let scheduleMap = new Map();

    // ---- Shift color helper (safe even if CSS changes) ----
    const __shiftBgCache = new Map();
    function getShiftBg(shiftId, shiftCode) {
        const id = Number(shiftId || 0);
        const key = id ? `id:${id}` : `code:${String(shiftCode || '')}`;
        if (__shiftBgCache.has(key)) return __shiftBgCache.get(key);

        // Reuse colors defined by .scha-cell + .scha-cX if available
        // Fallback to a light teal tone if CSS not found.
        const SHIFT_CLASSES = ["scha-c1", "scha-c2", "scha-c3", "scha-c4", "scha-c5", "scha-c6"];
        let cls = "";
        if (id > 0) cls = SHIFT_CLASSES[(id - 1) % SHIFT_CLASSES.length];

        const tmp = document.createElement("button");
        tmp.className = `scha-cell ${cls}`.trim();
        tmp.style.position = "absolute";
        tmp.style.left = "-99999px";
        tmp.style.top = "-99999px";
        tmp.style.width = "10px";
        tmp.style.height = "10px";
        tmp.style.opacity = "0";
        document.body.appendChild(tmp);

        const bg = getComputedStyle(tmp).backgroundColor || "";
        tmp.remove();

        const color = bg || "rgba(231,242,243,1)";
        __shiftBgCache.set(key, color);
        return color;
    }

    function renderHead() {
        const dayNames = ['Th 2', 'Th 3', 'Th 4', 'Th 5', 'Th 6', 'Th 7', 'CN'];
        head.innerHTML = days.map((d, idx) => `
            <div class="px-4 py-3 border-b border-[#cfe5e7] bg-[#f6fbfb]">
                <div class="text-xs font-extrabold text-[#0d1a1b]">${dayNames[idx]}</div>
                <div class="text-xs font-semibold text-[#4c939a]">${vnDate(d)}</div>
            </div>
        `).join('');
    }

    function buildShiftOptions(includeBlank) {
        const opts = [];
        if (includeBlank) opts.push(`<option value="">-- Thêm ca --</option>`);
        for (const x of shifts) {
            const id = x.shiftId ?? x.ShiftId;
            const code = x.shiftCode ?? x.ShiftCode ?? '';
            const name = x.shiftName ?? x.ShiftName ?? '';
            const st = (x.startTime ?? x.StartTime ?? '').toString().slice(0, 5);
            const et = (x.endTime ?? x.EndTime ?? '').toString().slice(0, 5);
            const label = `${code ? code + ' - ' : ''}${name}${st && et ? ` (${st}-${et})` : ''}`;
            opts.push(`<option value="${id}">${escapeHtml(label)}</option>`);
        }
        return opts.join('');
    }

    function groupSchedule(rawRows) {
        const m = new Map();
        for (const x of rawRows) {
            const wd = (x.workDate ?? x.WorkDate ?? '').toString().slice(0, 10);
            if (!wd) continue;

            const row = {
                scheduleId: x.scheduleId ?? x.ScheduleId ?? null,
                employeeId: x.employeeId ?? x.EmployeeId ?? null,
                workDate: wd,
                shiftId: x.shiftId ?? x.ShiftId ?? null,
                shiftCode: x.shiftCode ?? x.ShiftCode ?? '',
                shiftName: x.shiftName ?? x.ShiftName ?? '',
                startTime: ((x.startTime ?? x.StartTime) ?? '').toString().slice(0, 5),
                endTime: ((x.endTime ?? x.EndTime) ?? '').toString().slice(0, 5),
                note: x.note ?? x.Note ?? null,
            };

            if (!m.has(wd)) m.set(wd, []);
            m.get(wd).push(row);
        }

        // sort by startTime (if exists), then shiftId
        for (const [k, arr] of m.entries()) {
            arr.sort((a, b) => {
                const as = String(a.startTime || "");
                const bs = String(b.startTime || "");
                if (as && bs && as !== bs) return as.localeCompare(bs);
                return Number(a.shiftId || 0) - Number(b.shiftId || 0);
            });
        }
        return m;
    }

    function renderBody() {
        const addOptionsHtml = buildShiftOptions(true);

        body.innerHTML = days.map((d) => {
            const key = isoDate(d);
            const list = scheduleMap.get(key) || [];

            const chips = list.length
                ? list.map((it) => {
                    const bg = getShiftBg(it.shiftId, it.shiftCode);
                    const label = `${it.shiftCode ? it.shiftCode + ' • ' : ''}${it.shiftName || ''}`.trim();
                    const time = (it.startTime && it.endTime) ? `${it.startTime}-${it.endTime}` : '';
                    return `
                        <div class="flex items-center justify-between gap-2 px-2 py-1 rounded-lg border border-[#cfe5e7]"
                             style="background:${bg};">
                            <div class="min-w-0">
                                <div class="text-[11px] font-extrabold truncate text-[#0d1a1b]">${escapeHtml(label || 'Ca')}</div>
                                <div class="text-[11px] font-bold text-[#0d1a1b]/70">${escapeHtml(time || '—')}</div>
                                ${it.note ? `<div class="text-[11px] text-[#0d1a1b]/70 truncate">${escapeHtml(it.note)}</div>` : ``}
                            </div>
                            <div class="shrink-0 flex gap-1">
                                <button class="h-7 px-2 rounded-lg border border-[#cfe5e7] bg-white/70 hover:bg-white text-[11px] font-extrabold"
                                        data-act="edit" data-id="${escapeHtml(it.scheduleId)}" data-date="${escapeHtml(key)}">Sửa</button>
                                <button class="h-7 px-2 rounded-lg border border-red-200 bg-white/70 hover:bg-red-50 text-[11px] font-extrabold text-red-700"
                                        data-act="del" data-id="${escapeHtml(it.scheduleId)}" data-date="${escapeHtml(key)}">Xóa</button>
                            </div>
                        </div>
                    `;
                }).join('')
                : `<div class="text-xs font-bold text-slate-400">Chưa xếp ca</div>`;

            return `
                <div class="px-4 py-4 border-[#cfe5e7] border-r last:border-r-0 border-b">
                    <div class="flex flex-col gap-2" data-day="${escapeHtml(key)}">
                        ${chips}
                    </div>

                    <div class="mt-3 flex items-center gap-2">
                        <select data-act="add" data-workdate="${escapeHtml(key)}"
                            class="w-full h-10 rounded-xl border border-[#cfe5e7] px-3 text-sm font-semibold">
                            ${addOptionsHtml}
                        </select>
                        <button data-act="del-day" data-workdate="${escapeHtml(key)}"
                            class="h-10 px-3 rounded-xl border border-red-200 text-xs font-extrabold text-red-700 hover:bg-red-50"
                            title="Xóa toàn bộ ca trong ngày">
                            Xóa
                        </button>
                    </div>
                </div>
            `;
        }).join('');
    }

    // --- Modal (simple, generated if scheduling.html has none) ---
    let modal = document.getElementById('sch-modal');
    let modalTitle = document.getElementById('sch-modal-title');
    let modalShift = document.getElementById('sch-modal-shift');
    let modalNote = document.getElementById('sch-modal-note');
    let modalSave = document.getElementById('sch-modal-save');
    let modalCancel = document.getElementById('sch-modal-cancel');
    let modalErr = document.getElementById('sch-modal-error');

    const modalState = { scheduleId: null, workDate: null };

    function ensureModal() {
        if (modal) return;

        modal = document.createElement('div');
        modal.id = 'sch-modal';
        modal.className = 'hidden fixed inset-0 z-[60] bg-black/40 flex items-center justify-center p-4';
        modal.innerHTML = `
            <div class="w-full max-w-md rounded-2xl bg-white border border-[#cfe5e7] shadow-xl p-4">
                <div class="flex items-start justify-between gap-3">
                    <div>
                        <div id="sch-modal-title" class="text-base font-extrabold text-[#0d1a1b]">Sửa ca</div>
                        <div id="sch-modal-sub" class="text-xs font-bold text-slate-500 mt-1"></div>
                    </div>
                    <button id="sch-modal-cancel" class="h-8 px-3 rounded-xl border border-[#cfe5e7] text-xs font-extrabold hover:bg-[#e7f2f3]">Đóng</button>
                </div>

                <div id="sch-modal-error" class="hidden mt-3 p-3 rounded-xl border border-red-200 bg-red-50 text-red-700 text-sm font-semibold"></div>

                <div class="mt-4">
                    <label class="text-xs font-extrabold text-slate-600">Ca</label>
                    <select id="sch-modal-shift" class="mt-1 w-full h-10 rounded-xl border border-[#cfe5e7] px-3 text-sm font-semibold"></select>
                </div>

                <div class="mt-3">
                    <label class="text-xs font-extrabold text-slate-600">Ghi chú</label>
                    <input id="sch-modal-note" class="mt-1 w-full h-10 rounded-xl border border-[#cfe5e7] px-3 text-sm font-semibold" placeholder="(tuỳ chọn)" />
                </div>

                <div class="mt-4 flex items-center justify-end gap-2">
                    <button id="sch-modal-save" class="h-10 px-4 rounded-xl bg-[#0d1a1b] text-white text-xs font-extrabold hover:opacity-90">Lưu</button>
                </div>
            </div>
        `;
        document.body.appendChild(modal);

        modalTitle = document.getElementById('sch-modal-title');
        modalShift = document.getElementById('sch-modal-shift');
        modalNote = document.getElementById('sch-modal-note');
        modalSave = document.getElementById('sch-modal-save');
        modalCancel = document.getElementById('sch-modal-cancel');
        modalErr = document.getElementById('sch-modal-error');

        modal.addEventListener('click', (ev) => {
            if (ev.target === modal) closeModal();
        });
        modalCancel?.addEventListener('click', (ev) => { ev.preventDefault(); closeModal(); });

        modalSave?.addEventListener('click', async (ev) => {
            ev.preventDefault();
            await saveModal();
        });
    }

    function showModalError(msg) {
        ensureModal();
        if (!modalErr) return;
        if (!msg) {
            modalErr.classList.add('hidden');
            modalErr.textContent = '';
            return;
        }
        modalErr.classList.remove('hidden');
        modalErr.textContent = msg;
    }

    function openModalForEdit(workDate, item) {
        ensureModal();

        modalState.scheduleId = item?.scheduleId ?? null;
        modalState.workDate = workDate;

        const sub = document.getElementById('sch-modal-sub');
        if (sub) sub.textContent = `${workDate}`;

        if (modalTitle) modalTitle.textContent = item ? 'Sửa ca' : 'Thêm ca';

        // fill shift select
        modalShift.innerHTML = buildShiftOptions(false);
        if (item?.shiftId) modalShift.value = String(item.shiftId);
        modalNote.value = item?.note || '';

        showModalError('');
        modal.classList.remove('hidden');
    }

    function closeModal() {
        if (!modal) return;
        modal.classList.add('hidden');
        modalState.scheduleId = null;
        modalState.workDate = null;
    }

    async function saveModal() {
        const employeeId = selEmp ? Number(selEmp.value || 0) : 0;
        const workDate = modalState.workDate;
        const shiftId = Number(modalShift?.value || 0);
        const note = String(modalNote?.value || '').trim() || null;
        const scheduleId = modalState.scheduleId;

        if (!employeeId) return showModalError('Chưa chọn nhân viên.');
        if (!workDate) return showModalError('Thiếu ngày.');
        if (!shiftId) return showModalError('Chưa chọn ca.');

        modalSave && (modalSave.disabled = true);
        try {
            await window.bridge.invoke('SCHEDULE_UPSERT', {
                scheduleId: scheduleId || null,
                employeeId,
                workDate,
                shiftId,
                note
            }, 12000);

            await loadSchedule();
            closeModal();
            setStatus('ok', 'Đã lưu ca.');
            setTimeout(() => setStatus(null, ''), 1200);
        } catch (e) {
            console.error('SCHEDULE_UPSERT', e);
            showModalError(e?.message || 'Lưu thất bại.');
        } finally {
            modalSave && (modalSave.disabled = false);
        }
    }

    async function loadEmployees() {
        if (!selEmp) return;
        const res = await window.bridge.invoke('EMPLOYEE_LIST', { includeInactive: false }, 12000);
        const raw = Array.isArray(res.data) ? res.data : [];
        const list = raw.map(x => ({
            employeeId: x.employeeId ?? x.EmployeeId,
            employeeCode: x.employeeCode ?? x.EmployeeCode,
            fullName: x.fullName ?? x.FullName,
        })).filter(x => !!x.employeeId);

        selEmp.innerHTML = [
            `<option value="">-- Chọn nhân viên --</option>`,
            ...list.map(e => `<option value="${e.employeeId}">${escapeHtml((e.fullName || '').trim())}</option>`)
        ].join('');

        if (!selEmp.value && list.length > 0) selEmp.value = String(list[0].employeeId);
    }

    async function loadShifts() {
        const res = await window.bridge.invoke('SHIFT_LIST', { includeInactive: false }, 12000);
        shifts = Array.isArray(res.data) ? res.data : [];
    }

    async function loadSchedule() {
        renderHead();

        const employeeId = selEmp ? Number(selEmp.value || 0) : 0;
        if (!employeeId) {
            scheduleMap = new Map();
            renderBody();
            setStatus('info', 'Chọn nhân viên để xem/xếp lịch tuần.');
            return;
        }

        setStatus('info', 'Đang tải lịch...');
        const res = await window.bridge.invoke('SCHEDULE_LIST', { employeeId, dateFrom, dateTo }, 12000);
        const raw = Array.isArray(res.data) ? res.data : [];
        scheduleMap = groupSchedule(raw);

        renderBody();
        setStatus(null, '');
    }

    async function refreshAll() {
        try {
            setStatus('info', 'Đang tải dữ liệu...');
            await loadEmployees();
            await loadShifts();
            await loadSchedule();
        } catch (e) {
            console.error('[scheduling] refreshAll', e);
            setStatus('err', e?.message || 'Không tải được dữ liệu scheduling.');
            renderHead();
            renderBody();
        }
    }

    // Events
    selEmp?.addEventListener('change', () => loadSchedule());
    btnReload?.addEventListener('click', (ev) => { ev.preventDefault?.(); loadSchedule(); });

    // Add shift via select (does not overwrite existing)
    body.addEventListener('change', async (ev) => {
        const sel = ev.target?.closest?.('select[data-act="add"][data-workdate]');
        if (!sel) return;

        const employeeId = selEmp ? Number(selEmp.value || 0) : 0;
        const workDate = sel.getAttribute('data-workdate');
        const shiftId = Number(sel.value || 0);

        // reset select back to placeholder
        sel.value = '';

        if (!employeeId || !workDate || !shiftId) return;

        try {
            await window.bridge.invoke('SCHEDULE_UPSERT', { scheduleId: null, employeeId, workDate, shiftId, note: null }, 12000);
            await loadSchedule();
            setStatus('ok', 'Đã thêm ca.');
            setTimeout(() => setStatus(null, ''), 1200);
        } catch (e) {
            console.error('SCHEDULE_ADD', e);
            setStatus('err', e?.message || 'Thêm ca thất bại.');
            await loadSchedule();
        }
    });

    // Click actions (edit/delete 1 ca, delete whole day)
    body.addEventListener('click', async (ev) => {
        const btn = ev.target?.closest?.('button[data-act]');
        if (!btn) return;

        const act = btn.getAttribute('data-act');
        const employeeId = selEmp ? Number(selEmp.value || 0) : 0;

        if (act === 'del-day') {
            const workDate = btn.getAttribute('data-workdate');
            if (!employeeId || !workDate) return;
            if (!confirm('Xóa toàn bộ ca trong ngày này?')) return;

            try {
                await window.bridge.invoke('SCHEDULE_DELETE', { employeeId, workDate }, 12000);
                await loadSchedule();
                setStatus('ok', 'Đã xóa ca trong ngày.');
                setTimeout(() => setStatus(null, ''), 1200);
            } catch (e) {
                console.error('SCHEDULE_DELETE', e);
                setStatus('err', e?.message || 'Xóa thất bại.');
            }
            return;
        }

        const scheduleId = btn.getAttribute('data-id');
        const workDate = btn.getAttribute('data-date');

        if (act === 'del') {
            if (!scheduleId) return;
            if (!confirm('Xóa ca này?')) return;
            try {
                await window.bridge.invoke('SCHEDULE_DELETE_ID', { scheduleId: Number(scheduleId) }, 12000);
                await loadSchedule();
                setStatus('ok', 'Đã xóa ca.');
                setTimeout(() => setStatus(null, ''), 1200);
            } catch (e) {
                console.error('SCHEDULE_DELETE_ID', e);
                setStatus('err', e?.message || 'Xóa ca thất bại.');
            }
            return;
        }

        if (act === 'edit') {
            const list = scheduleMap.get(workDate) || [];
            const item = list.find(x => String(x.scheduleId) === String(scheduleId));
            openModalForEdit(workDate, item);
            return;
        }
    });

    // Init
    refreshAll();
};

// Alias (if page file is schedule.html)
window.pageInit.schedule = window.pageInit.scheduling;


window.pageInit = window.pageInit || {};
//---------------------------------------------------------------------------------
// scheduling_all (multi shifts per day)
window.pageInit.scheduling_all = function () {
    const thead = document.getElementById("scha-thead");
    const tbody = document.getElementById("scha-tbody");
    const weekLabel = document.getElementById("scha-week-label");
    const btnReload = document.getElementById("scha-reload");
    const chkInactive = document.getElementById("scha-include-inactive");
    const statusBox = document.getElementById("scha-status");

    // NEW: export excel
    const btnExportExcel = document.getElementById("scha-export-excel");

    // NEW: week switch + view switch
    const btnWeekCurrent = document.getElementById("scha-week-current");
    const btnWeekNext = document.getElementById("scha-week-next");
    const btnViewEmp = document.getElementById("scha-view-emp");
    const btnViewShift = document.getElementById("scha-view-shift");

    // modal (existing elements in scheduling_all.html, but we can extend)
    const modal = document.getElementById("scha-modal");
    const modalClose = document.getElementById("scha-modal-close");
    const modalCancel = document.getElementById("scha-cancel");
    const modalSave = document.getElementById("scha-save");
    const modalDel = document.getElementById("scha-del");
    const modalSub = document.getElementById("scha-modal-sub");
    const modalErr = document.getElementById("scha-modal-error");
    const selShift = document.getElementById("scha-shift");
    const inpNote = document.getElementById("scha-note");

    if (!thead || !tbody) return;

    const pad2 = (n) => String(n).padStart(2, "0");
    const toKey = (d) => `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
    const vn = (d) => `${pad2(d.getDate())}/${pad2(d.getMonth() + 1)}`;

    const SHIFT_CLASSES = ["scha-c1", "scha-c2", "scha-c3", "scha-c4", "scha-c5", "scha-c6"];
    function getShiftClass(shiftId, shiftCode) {
        const id = Number(shiftId || 0);
        if (id > 0) return SHIFT_CLASSES[(id - 1) % SHIFT_CLASSES.length];
        const s = String(shiftCode || "");
        let h = 0;
        for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
        return SHIFT_CLASSES[h % SHIFT_CLASSES.length];
    }

    // ===== Row color helpers (tô màu theo DÒNG CA) =====
    const __shiftRowBgCache = new Map();

    function __toRgbaWithAlpha(color, alpha) {
        if (!color) return "";
        const m = String(color).match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*[\d.]+)?\s*\)/i);
        if (!m) return color;
        const r = Number(m[1]), g = Number(m[2]), b = Number(m[3]);
        return `rgba(${r}, ${g}, ${b}, ${alpha})`;
    }

    function getRowBgFromShiftClass(shiftCls) {
        if (!shiftCls || shiftCls === "scha-off") return "";
        if (__shiftRowBgCache.has(shiftCls)) return __shiftRowBgCache.get(shiftCls);

        const tmp = document.createElement("button");
        tmp.className = `scha-cell ${shiftCls}`;
        tmp.style.position = "absolute";
        tmp.style.left = "-99999px";
        tmp.style.top = "-99999px";
        tmp.style.width = "10px";
        tmp.style.height = "10px";
        tmp.style.opacity = "0";
        document.body.appendChild(tmp);

        const bg = getComputedStyle(tmp).backgroundColor || "";
        tmp.remove();

        const rowBg = __toRgbaWithAlpha(bg, 0.18);
        __shiftRowBgCache.set(shiftCls, rowBg);
        return rowBg;
    }

    function cleanShiftName(name) {
        return String(name || "").replace(/\s*\(\s*\d{2}:\d{2}\s*-\s*\d{2}:\d{2}\s*\)\s*$/g, "").trim();
    }

    function setStatus(type, msg) {
        if (!statusBox) return;
        if (!msg) { statusBox.classList.add("hidden"); statusBox.textContent = ""; return; }
        statusBox.classList.remove("hidden");
        statusBox.className = "mt-4 p-4 rounded-xl border text-sm font-semibold " +
            (type === "ok" ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                : type === "err" ? "bg-red-50 text-red-700 border-red-200"
                    : "bg-slate-50 text-slate-700 border-slate-200");
        statusBox.textContent = msg;
    }

    // ===== Week helpers (Current week + Next week) =====
    function getMonday(d) {
        const x = new Date(d);
        const day = x.getDay(); // 0=CN,1=T2..6=T7
        const diff = (day === 0 ? -6 : 1 - day);
        x.setDate(x.getDate() + diff);
        x.setHours(0, 0, 0, 0);
        return x;
    }
    function addDays(d, n) {
        const x = new Date(d);
        x.setDate(x.getDate() + n);
        x.setHours(0, 0, 0, 0);
        return x;
    }
    function getWeekDays(weekIndex /*0=current,1=next*/) {
        const mon = getMonday(new Date());
        const start = weekIndex === 1 ? addDays(mon, 7) : mon;
        return Array.from({ length: 7 }, (_, i) => addDays(start, i));
    }

    let weekIndex = 0; // 0: tuần hiện tại, 1: tuần tiếp theo
    let viewMode = "emp"; // emp | shift

    let days = getWeekDays(weekIndex);
    let dateFrom = toKey(days[0]);
    let dateTo = toKey(days[6]);

    // state
    let employees = [];
    let shifts = [];
    // key: `${empId}|${workDate}` -> Array<scheduleRow>
    let map = new Map();

    // modal pick state (edit/add inside modal)
    let pick = { employeeId: 0, workDate: "", editingScheduleId: null };

    const escapeHtml = (s) => String(s ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");

    function setActiveBtn(btn, active) {
        if (!btn) return;
        btn.classList.toggle("bg-[#0d1a1b]", !!active);
        btn.classList.toggle("text-white", !!active);
        btn.classList.toggle("bg-white", !active);
        btn.classList.toggle("text-[#0d1a1b]", !active);
    }

    function syncWeekUI() {
        if (weekLabel) weekLabel.textContent = `Tuần: ${vn(days[0])} - ${vn(days[6])}`;
        setActiveBtn(btnWeekCurrent, weekIndex === 0);
        setActiveBtn(btnWeekNext, weekIndex === 1);
    }

    function syncViewUI() {
        setActiveBtn(btnViewEmp, viewMode === "emp");
        setActiveBtn(btnViewShift, viewMode === "shift");
    }

    function applyWeek(idx) {
        weekIndex = idx === 1 ? 1 : 0;
        days = getWeekDays(weekIndex);
        dateFrom = toKey(days[0]);
        dateTo = toKey(days[6]);
        syncWeekUI();
        loadAll();
    }

    function applyView(mode) {
        viewMode = mode === "shift" ? "shift" : "emp";
        syncViewUI();
        render();
    }

    // ===== modal helpers =====
    function showModalError(msg) {
        if (!modalErr) return;
        if (!msg) { modalErr.classList.add("hidden"); modalErr.textContent = ""; return; }
        modalErr.textContent = msg;
        modalErr.classList.remove("hidden");
    }

    function ensureModalListContainer() {
        if (!modal) return null;
        let list = modal.querySelector("#scha-list");
        if (!list) {
            // insert before note input if possible
            const wrap = document.createElement("div");
            wrap.className = "mt-3";
            wrap.innerHTML = `
                <div class="text-xs font-extrabold text-slate-600 mb-2">Ca trong ngày</div>
                <div id="scha-list" class="flex flex-col gap-2"></div>
                <div class="text-xs text-slate-500 font-bold mt-2">Tip: Bạn có thể thêm nhiều ca. Bấm “Sửa” để sửa ca cụ thể.</div>
            `;
            // try insert above shift selector
            const anchor = selShift?.parentElement || modal.querySelector("select")?.parentElement || modal.firstElementChild;
            if (anchor && anchor.parentElement) anchor.parentElement.insertBefore(wrap, anchor);
            else modal.appendChild(wrap);
            list = modal.querySelector("#scha-list");
        }
        return list;
    }

    function closeModal() { modal?.classList.add("hidden"); }

    function openModalForCell(employeeId, workDate) {
        pick.employeeId = employeeId;
        pick.workDate = workDate;
        pick.editingScheduleId = null;

        const emp = employees.find(x => x.employeeId === employeeId);
        if (modalSub) modalSub.textContent = `${emp?.fullName || ""} | ${workDate}`;
        showModalError("");

        // fill shift select
        if (selShift) {
            selShift.innerHTML = shifts.map(s => {
                const label = `${s.shiftCode} - ${cleanShiftName(s.shiftName)} (${s.startTime}-${s.endTime})`;
                return `<option value="${s.shiftId}">${escapeHtml(label)}</option>`;
            }).join("");
        }
        if (inpNote) inpNote.value = "";

        // render list
        renderModalList();

        // enable delete-day button only if has schedules
        const arr = map.get(`${employeeId}|${workDate}`) || [];
        if (modalDel) modalDel.disabled = arr.length === 0;

        modal?.classList.remove("hidden");
    }

    function renderModalList() {
        const listEl = ensureModalListContainer();
        if (!listEl) return;

        const arr = map.get(`${pick.employeeId}|${pick.workDate}`) || [];
        if (!arr.length) {
            listEl.innerHTML = `<div class="text-xs font-bold text-slate-400">Chưa có ca nào trong ngày.</div>`;
            return;
        }

        listEl.innerHTML = arr.map(it => {
            const cls = getShiftClass(it.shiftId, it.shiftCode);
            const rowBg = getRowBgFromShiftClass(cls);
            const label = `${it.shiftCode ? it.shiftCode + ' • ' : ''}${cleanShiftName(it.shiftName || '')}`;
            const time = (it.startTime && it.endTime) ? `${it.startTime}-${it.endTime}` : "—";
            return `
                <div class="p-2 rounded-xl border border-[#cfe5e7] flex items-center justify-between gap-2" style="background-color:${rowBg || 'transparent'}">
                    <div class="min-w-0">
                        <div class="text-[12px] font-extrabold text-[#0d1a1b] truncate">${escapeHtml(label)}</div>
                        <div class="text-[11px] font-bold text-[#0d1a1b]/70">${escapeHtml(time)}</div>
                        ${it.note ? `<div class="text-[11px] text-[#0d1a1b]/70 truncate">${escapeHtml(it.note)}</div>` : ``}
                    </div>
                    <div class="shrink-0 flex gap-1">
                        <button class="h-8 px-2 rounded-xl border border-[#cfe5e7] bg-white/70 hover:bg-white text-[11px] font-extrabold"
                            data-act="edit" data-id="${escapeHtml(it.scheduleId)}">Sửa</button>
                        <button class="h-8 px-2 rounded-xl border border-red-200 bg-white/70 hover:bg-red-50 text-[11px] font-extrabold text-red-700"
                            data-act="del" data-id="${escapeHtml(it.scheduleId)}">Xóa</button>
                    </div>
                </div>
            `;
        }).join("");
    }

    async function modalSaveUpsert() {
        try {
            const shiftId = Number(selShift?.value || 0);
            const note = (inpNote?.value || "").trim() || null;

            if (!pick.employeeId || !pick.workDate) return showModalError("Thiếu thông tin.");
            if (!shiftId) return showModalError("Chưa chọn ca.");

            await window.bridge.invoke("SCHEDULE_UPSERT", {
                scheduleId: pick.editingScheduleId || null,
                employeeId: pick.employeeId,
                workDate: pick.workDate,
                shiftId,
                note
            }, 12000);

            await loadAll();
            // keep modal open to add more
            pick.editingScheduleId = null;
            if (inpNote) inpNote.value = "";
            renderModalList();
            showModalError("");
            setStatus("ok", "Đã lưu ca.");
            setTimeout(() => setStatus(null, ""), 900);
        } catch (e) {
            showModalError(e?.message || "Lưu lịch thất bại.");
        }
    }

    async function modalDeleteDay() {
        try {
            if (!pick.employeeId || !pick.workDate) return;
            if (!confirm("Xóa toàn bộ ca trong ngày này?")) return;

            await window.bridge.invoke("SCHEDULE_DELETE", {
                employeeId: pick.employeeId,
                workDate: pick.workDate
            }, 12000);

            await loadAll();
            renderModalList();
            closeModal();
            setStatus("ok", "Đã xóa lịch ngày.");
            setTimeout(() => setStatus(null, ""), 1200);
        } catch (e) {
            showModalError(e?.message || "Xóa lịch thất bại.");
        }
    }

    async function modalDeleteOne(scheduleId) {
        if (!scheduleId) return;
        if (!confirm("Xóa ca này?")) return;
        try {
            await window.bridge.invoke("SCHEDULE_DELETE_ID", { scheduleId: Number(scheduleId) }, 12000);
            await loadAll();
            renderModalList();
            setStatus("ok", "Đã xóa ca.");
            setTimeout(() => setStatus(null, ""), 900);
        } catch (e) {
            showModalError(e?.message || "Xóa ca thất bại.");
        }
    }

    function modalStartEdit(scheduleId) {
        const arr = map.get(`${pick.employeeId}|${pick.workDate}`) || [];
        const it = arr.find(x => String(x.scheduleId) === String(scheduleId));
        if (!it) return;

        pick.editingScheduleId = it.scheduleId;

        if (selShift) selShift.value = String(it.shiftId);
        if (inpNote) inpNote.value = it.note || "";
    }

    // ===== Render =====
    function renderHead(firstColLabel) {
        const dayNames = ["T2", "T3", "T4", "T5", "T6", "T7", "CN"];
        thead.innerHTML = `
            <tr>
              <th class="sticky top-0 left-0 z-20 bg-white border-b border-[#cfe5e7] px-4 py-3 text-left">
                ${escapeHtml(firstColLabel)}
              </th>
              ${days.map((d, i) => `
                <th class="sticky top-0 z-10 bg-white border-b border-[#cfe5e7] px-4 py-3 text-left">
                  <div class="font-extrabold">${dayNames[i]}</div>
                  <div class="text-xs text-slate-500 font-bold">${vn(d)}</div>
                </th>
              `).join("")}
            </tr>
          `;
    }

    function renderByEmployee() {
        renderHead("Nhân viên");

        tbody.innerHTML = employees.map(emp => {
            const empCode = escapeHtml(emp.employeeCode || "");
            const empName = escapeHtml(emp.fullName || "");

            const cells = days.map((d) => {
                const wd = toKey(d);
                const arr = map.get(`${emp.employeeId}|${wd}`) || [];

                const content = arr.length
                    ? `<div class="flex flex-col gap-1">
                        ${arr.map(it => {
                        const cls = getShiftClass(it.shiftId, it.shiftCode);
                        const bg = getRowBgFromShiftClass(cls) || "rgba(231,242,243,0.6)";
                        const label = `${it.shiftCode ? it.shiftCode + ' • ' : ''}${cleanShiftName(it.shiftName || '')}`;
                        const time = (it.startTime && it.endTime) ? `${it.startTime}-${it.endTime}` : "—";
                        return `
                                <div class="px-2 py-1 rounded-lg border border-[#cfe5e7]" style="background-color:${bg}">
                                    <div class="text-[11px] font-extrabold text-[#0d1a1b] truncate">${escapeHtml(label)}</div>
                                    <div class="text-[11px] font-bold text-[#0d1a1b]/70">${escapeHtml(time)}</div>
                                </div>
                            `;
                    }).join("")}
                       </div>`
                    : `<div class="text-xs font-bold text-slate-400">Nghỉ</div>`;

                // class: if single shift, color button by shift; if multi, neutral
                const btnCls = arr.length === 1
                    ? `scha-cell ${getShiftClass(arr[0].shiftId, arr[0].shiftCode)}`
                    : `scha-cell`;

                return `
                    <td class="border-b border-[#cfe5e7] px-3 py-3 align-top">
                      <button
                        data-emp="${emp.employeeId}"
                        data-date="${wd}"
                        class="${btnCls}"
                        type="button"
                        style="min-height:72px;"
                        title="Bấm để thêm/sửa ca"
                      >
                        ${content}
                      </button>
                    </td>
                  `;
            }).join("");

            return `
                  <tr>
                    <td class="sticky left-0 z-10 bg-white border-b border-[#cfe5e7] px-4 py-3 whitespace-nowrap">
                      <div class="font-extrabold">${empCode}</div>
                      <div class="text-xs text-slate-500 font-bold">${empName}</div>
                    </td>
                    ${cells}
                  </tr>
                `;
        }).join("");
    }

    function renderByShift() {
        renderHead("Ca làm");

        const shiftRows = [
            ...shifts.map(s => ({
                shiftId: Number(s.shiftId),
                shiftCode: s.shiftCode,
                shiftName: s.shiftName,
                startTime: s.startTime,
                endTime: s.endTime
            })),
            { shiftId: 0, shiftCode: "OFF", shiftName: "Nghỉ", startTime: "", endTime: "" }
        ];

        shiftRows.sort((a, b) => (a.shiftId === 0) - (b.shiftId === 0) || a.shiftId - b.shiftId);

        const pillBtn = (empId, workDate, name, code) => `
            <button
              type="button"
              data-emp="${empId}"
              data-date="${workDate}"
              title="${escapeHtml(code)} - ${escapeHtml(name)}"
              class="px-2 py-1 rounded-lg border border-[#cfe5e7] text-[11px] font-bold hover:bg-[#e7f2f3]"
              style="max-width:150px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;background:rgba(255,255,255,0.75);"
            >${escapeHtml(name)}</button>
        `;

        tbody.innerHTML = shiftRows.map(sr => {
            const isOff = Number(sr.shiftId) === 0;
            const shiftCls = isOff ? "scha-off" : getShiftClass(sr.shiftId, sr.shiftCode);
            const rowBg = isOff ? "" : getRowBgFromShiftClass(shiftCls);

            const shiftLabel = isOff
                ? `<div class="font-extrabold">Nghỉ</div><div class="text-xs text-slate-500 font-bold">—</div>`
                : `<div class="font-extrabold">${escapeHtml(sr.shiftCode || "")}</div>
                   <div class="text-xs text-slate-500 font-bold">${escapeHtml(cleanShiftName(sr.shiftName || ""))}</div>
                   <div class="text-xs text-slate-500 font-bold">${escapeHtml((sr.startTime || ""))}-${escapeHtml((sr.endTime || ""))}</div>`;

            const cells = days.map(d => {
                const wd = toKey(d);

                // collect employees for this shift at this date
                const list = [];
                for (const emp of employees) {
                    const arr = map.get(`${emp.employeeId}|${wd}`) || [];
                    const has = arr.some(sc => Number(sc.shiftId || 0) === Number(sr.shiftId));
                    if (has) list.push(emp);
                    if (isOff && arr.length === 0) list.push(emp);
                }

                // unique + sort by name
                const uniq = [];
                const seen = new Set();
                for (const e of list) {
                    if (seen.has(e.employeeId)) continue;
                    seen.add(e.employeeId);
                    uniq.push(e);
                }
                uniq.sort((a, b) => String(a.fullName || "").localeCompare(String(b.fullName || ""), "vi"));

                const inner = uniq.length === 0
                    ? `<div class="text-xs text-slate-400 font-bold">—</div>`
                    : `<div class="flex flex-wrap gap-1">
                        ${uniq.map(e => pillBtn(e.employeeId, wd, e.fullName || "", e.employeeCode || "")).join("")}
                       </div>`;

                return `<td class="border-b border-[#cfe5e7] px-3 py-3 align-top">${inner}</td>`;
            }).join("");

            const leftTdStyle = isOff ? `style="background-color:white;"` : `style="background-color:${rowBg};"`;
            const trStyle = isOff ? "" : `style="background-color:${rowBg};"`;

            return `
                <tr ${trStyle}>
                    <td class="sticky left-0 z-10 border-b border-[#cfe5e7] px-4 py-3 whitespace-nowrap" ${leftTdStyle}>
                        ${shiftLabel}
                    </td>
                    ${cells}
                </tr>
            `;
        }).join("");
    }

    function render() {
        syncWeekUI();
        syncViewUI();
        if (viewMode === "shift") renderByShift();
        else renderByEmployee();
    }

    // ===== Load =====
    async function loadAll() {
        try {
            setStatus("info", "Đang tải lịch tuần...");
            const includeInactiveEmployees = !!chkInactive?.checked;

            const [empRes, shiftRes, weekRes] = await Promise.all([
                window.bridge.invoke("EMPLOYEE_LIST", { includeInactive: includeInactiveEmployees }, 12000),
                window.bridge.invoke("SHIFT_LIST", { includeInactive: false }, 12000),
                window.bridge.invoke("SCHEDULE_WEEK_ALL", { dateFrom, dateTo, includeInactiveEmployees }, 12000),
            ]);

            employees = (Array.isArray(empRes.data) ? empRes.data : []).map(x => ({
                employeeId: Number(x.employeeId ?? x.EmployeeId),
                employeeCode: x.employeeCode ?? x.EmployeeCode,
                fullName: x.fullName ?? x.FullName,
                isActive: Boolean(x.isActive ?? x.IsActive),
            })).filter(x => x.employeeId > 0);

            shifts = (Array.isArray(shiftRes.data) ? shiftRes.data : []).map(x => ({
                shiftId: Number(x.shiftId ?? x.ShiftId),
                shiftCode: x.shiftCode ?? x.ShiftCode,
                shiftName: x.shiftName ?? x.ShiftName,
                startTime: ((x.startTime ?? x.StartTime) ?? "").slice(0, 5),
                endTime: ((x.endTime ?? x.EndTime) ?? "").slice(0, 5),
            }));

            map = new Map();
            const list = Array.isArray(weekRes.data) ? weekRes.data : [];
            for (const x of list) {
                const employeeId = Number(x.EmployeeId ?? x.employeeId);
                const workDate = ((x.WorkDate ?? x.workDate) ?? "").slice(0, 10);
                if (!employeeId || !workDate) continue;

                const row = {
                    scheduleId: x.ScheduleId ?? x.scheduleId ?? null,
                    employeeId,
                    workDate,
                    shiftId: Number(x.ShiftId ?? x.shiftId),
                    shiftCode: x.ShiftCode ?? x.shiftCode,
                    shiftName: x.ShiftName ?? x.shiftName,
                    startTime: ((x.StartTime ?? x.startTime) ?? "").slice(0, 5),
                    endTime: ((x.EndTime ?? x.endTime) ?? "").slice(0, 5),
                    note: x.Note ?? x.note,
                };

                const k = `${employeeId}|${workDate}`;
                if (!map.has(k)) map.set(k, []);
                map.get(k).push(row);
            }

            // sort each day list by time
            for (const [k, arr] of map.entries()) {
                arr.sort((a, b) => {
                    const as = String(a.startTime || "");
                    const bs = String(b.startTime || "");
                    if (as && bs && as !== bs) return as.localeCompare(bs);
                    return Number(a.shiftId || 0) - Number(b.shiftId || 0);
                });
            }

            render();
            setStatus(null, "");
        } catch (e) {
            console.error(e);
            setStatus("err", e?.message || "Không tải được lịch tuần.");
        }
    }

    // ===== events =====
    btnReload?.addEventListener("click", loadAll);
    chkInactive?.addEventListener("change", loadAll);

    btnWeekCurrent?.addEventListener("click", () => applyWeek(0));
    btnWeekNext?.addEventListener("click", () => applyWeek(1));

    btnViewEmp?.addEventListener("click", () => applyView("emp"));
    btnViewShift?.addEventListener("click", () => applyView("shift"));

    // open modal from table cell (emp view) or pill (shift view)
    tbody.addEventListener("click", (ev) => {
        const btn = ev.target.closest("button[data-emp][data-date]");
        if (!btn) return;
        openModalForCell(Number(btn.dataset.emp), btn.dataset.date);
    });

    // modal close
    modalClose?.addEventListener("click", closeModal);
    modalCancel?.addEventListener("click", closeModal);
    modal?.addEventListener("click", (ev) => {
        if (ev.target?.dataset?.schaClose === "1") closeModal();
    });

    // modal save = add/edit a single schedule item
    modalSave?.addEventListener("click", async () => {
        await modalSaveUpsert();
    });

    // modal delete day
    modalDel?.addEventListener("click", async () => {
        await modalDeleteDay();
    });

    // clicks inside modal list (edit/delete)
    modal?.addEventListener("click", (ev) => {
        const b = ev.target.closest("button[data-act][data-id]");
        if (!b) return;
        const act = b.getAttribute("data-act");
        const id = b.getAttribute("data-id");
        if (act === "edit") return modalStartEdit(id);
        if (act === "del") return modalDeleteOne(id);
    });
    // ===== export excel (week schedule) =====
    async function exportWeekExcel() {
        const includeInactiveEmployees = !!chkInactive?.checked;

        btnExportExcel && (btnExportExcel.disabled = true);
        setStatus("info", "Đang xuất Excel lịch tuần...");

        try {
            // C# sẽ mở SaveFileDialog để chọn nơi lưu
            const res = await window.bridge.invoke(
                "SCHEDULE_WEEK_EXPORT_EXCEL",
                { dateFrom, dateTo, includeInactiveEmployees },
                60000
            );

            const savedPath = res?.data?.path || res?.data?.Path || "";
            setStatus("ok", savedPath ? `Xuất Excel thành công: ${savedPath}` : "Xuất Excel thành công.");
        } catch (e) {
            console.error("SCHEDULE_WEEK_EXPORT_EXCEL", e);
            setStatus("err", e?.message || "Xuất Excel thất bại.");
        } finally {
            btnExportExcel && (btnExportExcel.disabled = false);
        }
    }

    btnExportExcel?.addEventListener("click", (ev) => {
        ev.preventDefault?.();
        exportWeekExcel();
    });



    // init
    syncWeekUI();
    syncViewUI();
    loadAll();
};


// ===== Payroll page init =====
window.pageInit = window.pageInit || {};

window.pageInit.payroll = function () {
    const elFrom = document.getElementById('pr-from');
    const elTo = document.getElementById('pr-to');
    const elEmp = document.getElementById('pr-emp');
    const btnLoad = document.getElementById('pr-load');
    const btnExport = document.getElementById('pr-export');
    const btnExportEmp = document.getElementById('pr-export-emp');
    const tbody = document.getElementById('pr-tbody');
    const txtSearch = document.getElementById('pr-search');
    const statusBox = document.getElementById('pr-status');

    if (!tbody) return;

    const kHours = document.getElementById('pr-kpi-hours');
    const kLate = document.getElementById('pr-kpi-late');
    const kGross = document.getElementById('pr-kpi-gross');
    const kPenalty = document.getElementById('pr-kpi-penalty');
    const kNet = document.getElementById('pr-kpi-net');

    const kMtdPenalty = document.getElementById('pr-kpi-mtd-penalty');
    const kMtdNet = document.getElementById('pr-kpi-mtd-net');

    let allEmployees = [];
    let allRows = [];
    let filteredRows = [];

    const escapeHtml = (s) => String(s ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');

    const fmtMoney = (n) => {
        const v = Number(n ?? 0);
        if (!Number.isFinite(v)) return '0';
        return v.toLocaleString('vi-VN');
    };

    const fmtDate = (d) => {
        if (!d) return '';
        // d is ISO string or Date
        const dt = (d instanceof Date) ? d : new Date(d);
        if (Number.isNaN(dt.getTime())) return '';
        const yyyy = dt.getFullYear();
        const mm = String(dt.getMonth() + 1).padStart(2, '0');
        const dd = String(dt.getDate()).padStart(2, '0');
        return `${yyyy}-${mm}-${dd}`;
    };
    // ===== Payroll cycle 10 -> 10 (snap range) =====
    const pad2 = (n) => String(n).padStart(2, '0');

    const toYmd = (d) => {
        const yyyy = d.getFullYear();
        const mm = pad2(d.getMonth() + 1);
        const dd = pad2(d.getDate());
        return `${yyyy}-${mm}-${dd}`;
    };

    const parseYmd = (ymd) => {
        if (!ymd) return null;
        const m = String(ymd).trim().match(/^(\d{4})-(\d{2})-(\d{2})$/);
        if (!m) return null;
        const y = Number(m[1]), mo = Number(m[2]), da = Number(m[3]);
        const dt = new Date(y, mo - 1, da);
        return Number.isNaN(dt.getTime()) ? null : dt;
    };

    const addMonthsSafe = (date, months) => {
        const d = new Date(date.getFullYear(), date.getMonth(), date.getDate());
        const day = d.getDate();
        d.setMonth(d.getMonth() + months);
        // nếu tháng mới không có "day" (vd 31) -> lùi về cuối tháng
        if (d.getDate() !== day) d.setDate(0);
        return d;
    };

    // baseDate => trả về from=ngày 10, to=ngày 9 của tháng kế (inclusive)
    const resolveCycle10to10 = (baseDate) => {
        const d = new Date(baseDate.getFullYear(), baseDate.getMonth(), baseDate.getDate());

        let from = new Date(d.getFullYear(), d.getMonth(), 10);
        if (d.getDate() < 10) from = addMonthsSafe(from, -1);

        const toExclusive = addMonthsSafe(from, 1);     // 10 tháng sau
        const toInclusive = new Date(toExclusive);
        toInclusive.setDate(toInclusive.getDate() - 1); // 9 tháng sau

        return {
            fromYmd: toYmd(from),
            toYmd: toYmd(toInclusive),
        };
    };

    // Snap theo ngày user đang chọn: ưu tiên elTo, rồi elFrom, rồi today
    function snapRange10to10() {
        const base =
            parseYmd(elTo?.value) ||
            parseYmd(elFrom?.value) ||
            new Date();

        const r = resolveCycle10to10(base);

        // cập nhật UI để user thấy kỳ đã được gom
        if (elFrom) elFrom.value = r.fromYmd;
        if (elTo) elTo.value = r.toYmd;

        return { dateFrom: r.fromYmd, dateTo: r.toYmd };
    }


    const fmtTime = (d) => {
        if (!d) return '';
        const dt = (d instanceof Date) ? d : new Date(d);
        if (Number.isNaN(dt.getTime())) return '';
        const hh = String(dt.getHours()).padStart(2, '0');
        const mi = String(dt.getMinutes()).padStart(2, '0');
        return `${hh}:${mi}`;
    };

    function setStatus(type, msg) {
        if (!statusBox) return;
        if (!msg) {
            statusBox.classList.add('hidden');
            statusBox.textContent = '';
            return;
        }
        statusBox.classList.remove('hidden');
        statusBox.className =
            'mt-4 p-4 rounded-xl border text-sm font-semibold ' +
            (type === 'ok' ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
                : type === 'err' ? 'bg-red-50 text-red-700 border-red-200'
                    : 'bg-slate-50 text-slate-700 border-slate-200');
        statusBox.textContent = msg;
    }

    function setDefaults() {
        // mặc định chỉ hiển thị hoạt động "hôm nay"
        // (nếu user đã chọn ngày rồi thì không tự ghi đè)
        if ((elFrom && elFrom.value) || (elTo && elTo.value)) return;

        const today = new Date();
        const ymdToday = toYmd(new Date(today.getFullYear(), today.getMonth(), today.getDate()));
        if (elFrom) elFrom.value = ymdToday;
        if (elTo) elTo.value = ymdToday;
    }

    function renderKpis(rows) {
        // ===== KPI theo danh sách đang hiển thị (filteredRows) =====
        const totalMinutes = rows.reduce((a, r) => a + Number(r.minutesWorked ?? r.MinutesWorked ?? 0), 0);
        const totalLate = rows.reduce((a, r) => a + Number(r.lateMinutes ?? r.LateMinutes ?? 0), 0);
        const gross = rows.reduce((a, r) => a + Number(r.grossPay ?? r.GrossPay ?? 0), 0);
        const penalty = rows.reduce((a, r) => a + Number(r.penaltyAmount ?? r.PenaltyAmount ?? 0), 0);
        const net = rows.reduce((a, r) => a + Number(r.netPay ?? r.NetPay ?? 0), 0);

        if (kHours) kHours.textContent = `${(totalMinutes / 60).toFixed(1)}h`;
        if (kLate) kLate.textContent = `${totalLate.toLocaleString('vi-VN')} phút`;
        if (kGross) kGross.textContent = fmtMoney(gross);
        if (kPenalty) kPenalty.textContent = fmtMoney(penalty);
        if (kNet) kNet.textContent = fmtMoney(net);

        // ===== KPI "tháng/kỳ 10->10 đến hiện tại" (MTD) =====
        // - lấy dữ liệu từ allRows (đã theo nhân viên chọn + kỳ đang load)
        // - chỉ tính từ pr-from (ngày 10) đến min(today, pr-to)
        const fromDate = parseYmd(elFrom?.value);
        const toDate = parseYmd(elTo?.value);

        // today ở dạng yyyy-mm-dd để so sánh date-only (tránh lệch giờ)
        const today = new Date();
        const todayDateOnly = new Date(today.getFullYear(), today.getMonth(), today.getDate());

        // mtdEnd = min(today, toDate)
        let mtdEnd = todayDateOnly;
        if (toDate && toDate.getTime() < mtdEnd.getTime()) mtdEnd = toDate;

        const base = Array.isArray(allRows) ? allRows : [];

        const getWorkDateOnly = (r) => {
            const raw = r.workDate ?? r.WorkDate;
            if (!raw) return null;

            // thường WorkDate là "YYYY-MM-DD..." => cắt 10 ký tự
            const ymd = String(raw).slice(0, 10);
            const d = parseYmd(ymd);
            return d || null;
        };

        const inRange = (d) => {
            if (!d) return false;
            if (fromDate && d.getTime() < fromDate.getTime()) return false;
            if (mtdEnd && d.getTime() > mtdEnd.getTime()) return false;
            return true;
        };

        let mtdGross = 0;
        let mtdPenalty = 0;

        for (const r of base) {
            const wd = getWorkDateOnly(r);
            if (!inRange(wd)) continue;

            mtdGross += Number(r.grossPay ?? r.GrossPay ?? 0);
            mtdPenalty += Number(r.penaltyAmount ?? r.PenaltyAmount ?? 0);
        }

        const mtdNet = mtdGross - mtdPenalty; // đúng yêu cầu: tổng gross - tổng phạt

        if (kMtdPenalty) kMtdPenalty.textContent = fmtMoney(mtdPenalty);
        if (kMtdNet) kMtdNet.textContent = fmtMoney(mtdNet);
    }


    function renderTable(rows) {
        if (!tbody) return;
        if (!rows.length) {
            tbody.innerHTML = `
                <tr>
                    <td colspan="11" class="px-6 py-10 text-center text-sm text-[#4c939a]">
                        Không có dữ liệu.
                    </td>
                </tr>`;
            return;
        }

        tbody.innerHTML = rows.map(r => {
            const workDate = r.workDate ?? r.WorkDate;
            const code = r.employeeCode ?? r.EmployeeCode;
            const name = r.fullName ?? r.FullName;
            const shift = r.shiftCode ?? r.ShiftCode;
            const inTime = r.checkInTime ?? r.CheckInTime;
            const outTime = r.checkOutTime ?? r.CheckOutTime;
            const minutes = Number(r.minutesWorked ?? r.MinutesWorked ?? 0);
            const late = Number(r.lateMinutes ?? r.LateMinutes ?? 0);
            const gross = Number(r.grossPay ?? r.GrossPay ?? 0);
            const penalty = Number(r.penaltyAmount ?? r.PenaltyAmount ?? 0);
            const net = Number(r.netPay ?? r.NetPay ?? 0);

            return `
                <tr class="hover:bg-[#e7f2f3]/40 dark:hover:bg-[#1a3538]/40">
                    <td class="px-6 py-3 whitespace-nowrap">${escapeHtml(fmtDate(workDate))}</td>
                    <td class="px-6 py-3 whitespace-nowrap font-semibold">${escapeHtml(code)}</td>
                    <td class="px-6 py-3 whitespace-nowrap">${escapeHtml(name)}</td>
                    <td class="px-6 py-3 whitespace-nowrap">${escapeHtml(shift)}</td>
                    <td class="px-6 py-3 whitespace-nowrap">${escapeHtml(fmtTime(inTime))}</td>
                    <td class="px-6 py-3 whitespace-nowrap">${escapeHtml(fmtTime(outTime))}</td>
                    <td class="px-6 py-3 whitespace-nowrap text-right">${(minutes / 60).toFixed(2)}</td>
                    <td class="px-6 py-3 whitespace-nowrap text-right ${late > 0 ? 'text-red-600 font-bold' : ''}">${late.toLocaleString('vi-VN')}</td>
                    <td class="px-6 py-3 whitespace-nowrap text-right">${fmtMoney(gross)}</td>
                    <td class="px-6 py-3 whitespace-nowrap text-right ${penalty > 0 ? 'text-red-600 font-bold' : ''}">${fmtMoney(penalty)}</td>
                    <td class="px-6 py-3 whitespace-nowrap text-right font-bold">${fmtMoney(net)}</td>
                </tr>`;
        }).join('');
    }

    function applyFilter() {
        const q = String(txtSearch?.value || '').trim().toLowerCase();
        if (!q) {
            filteredRows = allRows.slice();
        } else {
            filteredRows = allRows.filter(r => {
                const code = String(r.employeeCode ?? r.EmployeeCode ?? '').toLowerCase();
                const name = String(r.fullName ?? r.FullName ?? '').toLowerCase();
                const shift = String(r.shiftCode ?? r.ShiftCode ?? '').toLowerCase();
                return code.includes(q) || name.includes(q) || shift.includes(q);
            });
        }
        renderKpis(filteredRows);
        renderTable(filteredRows);
    }

    async function loadEmployees() {
        try {
            const res = await window.bridge.invoke('EMPLOYEE_LIST', { includeInactive: false }, 12000);
            allEmployees = Array.isArray(res.data) ? res.data : [];

            if (elEmp) {
                // keep first option
                const keepFirst = elEmp.querySelector('option[value=""]');
                elEmp.innerHTML = '';
                if (keepFirst) elEmp.appendChild(keepFirst);
                allEmployees
                    .map(x => ({
                        employeeId: x.employeeId ?? x.EmployeeId,
                        employeeCode: x.employeeCode ?? x.EmployeeCode,
                        fullName: x.fullName ?? x.FullName
                    }))
                    .sort((a, b) => String(a.employeeCode).localeCompare(String(b.employeeCode)))
                    .forEach(emp => {
                        const opt = document.createElement('option');
                        opt.value = String(emp.employeeId);
                        opt.textContent = `${emp.fullName}`;
                        elEmp.appendChild(opt);
                    });
            }
        } catch (e) {
            console.error('EMPLOYEE_LIST', e);
            // not fatal
        }
    }

    async function loadPayroll(mode) {
        // mode:
        // - 'today': luôn tải dữ liệu của hôm nay (và set From/To = hôm nay)
        // - 'range': tải theo From/To người dùng chọn
        const m = String(mode || 'range');

        let dateFrom = '';
        let dateTo = '';

        if (m === 'today') {
            const today = new Date();
            const ymdToday = toYmd(new Date(today.getFullYear(), today.getMonth(), today.getDate()));
            dateFrom = ymdToday;
            dateTo = ymdToday;
            if (elFrom) elFrom.value = ymdToday;
            if (elTo) elTo.value = ymdToday;
        } else {
            dateFrom = String(elFrom?.value || '').trim();
            dateTo = String(elTo?.value || '').trim();
        }

        const employeeIdRaw = String(elEmp?.value || '').trim();
        const employeeId = employeeIdRaw ? Number(employeeIdRaw) : null;

        if (!dateFrom || !dateTo) {
            setStatus('err', 'Vui lòng chọn Từ ngày / Đến ngày.');
            return;
        }

        btnLoad && (btnLoad.disabled = true);
        setStatus('info', 'Đang tải dữ liệu...');

        try {
            const res = await window.bridge.invoke('PAYROLL_PREVIEW', {
                dateFrom,
                dateTo,
                employeeId
            }, 20000);

            allRows = Array.isArray(res.data) ? res.data : [];
            setStatus(null, '');
            applyFilter();
        } catch (e) {
            console.error('PAYROLL_PREVIEW', e);
            allRows = [];
            applyFilter();
            setStatus('err', e?.message || 'Không tải được payroll (xem console).');
        } finally {
            btnLoad && (btnLoad.disabled = false);
        }
    }

    function getSelectedEmployeeId() {
        const employeeIdRaw = String(elEmp?.value || '').trim();
        return employeeIdRaw ? Number(employeeIdRaw) : null;
    }

    function updateExportEmpState() {
        const empId = getSelectedEmployeeId();
        if (btnExportEmp) btnExportEmp.disabled = !empId;
    }

    async function exportExcelAll() {
        /*const dateFrom = String(elFrom?.value || '').trim();
        const dateTo = String(elTo?.value || '').trim();*/

        /*const { dateFrom, dateTo } = snapRange10to10();*/
        const dateFrom = String(elFrom?.value || '').trim();
        const dateTo = String(elTo?.value || '').trim();


        if (!dateFrom || !dateTo) {
            setStatus('err', 'Vui lòng chọn Từ ngày / Đến ngày trước khi xuất Excel.');
            return;
        }

        btnExport && (btnExport.disabled = true);
        setStatus('info', 'Đang xuất Excel (tất cả nhân viên)...');

        try {
            const res = await window.bridge.invoke('PAYROLL_EXPORT_EXCEL', {
                dateFrom,
                dateTo,
                employeeId: null,
                splitToSheetsByEmployee: true
            }, 60000);


            if (res?.data?.cancelled) {
                setStatus('info', 'Đã hủy xuất Excel.');
                return;
            }

            const path = res?.data?.path || res?.data?.Path || '';
            setStatus('ok', path ? `Xuất Excel thành công: ${path}` : 'Xuất Excel thành công.');
        } catch (e) {
            console.error('PAYROLL_EXPORT_EXCEL', e);
            setStatus('err', e?.message || 'Xuất Excel thất bại (xem console).');
        } finally {
            btnExport && (btnExport.disabled = false);
        }
    }

    async function exportExcelSelectedEmployee() {
        /*  const dateFrom = String(elFrom?.value || '').trim();
          const dateTo = String(elTo?.value || '').trim();
          const employeeId = getSelectedEmployeeId();*/

        const dateFrom = String(elFrom?.value || '').trim();
        const dateTo = String(elTo?.value || '').trim();


/*        const { dateFrom, dateTo } = snapRange10to10();*/
        const employeeId = getSelectedEmployeeId();


        if (!dateFrom || !dateTo) {
            setStatus('err', 'Vui lòng chọn Từ ngày / Đến ngày trước khi xuất Excel.');
            return;
        }
        if (!employeeId) {
            setStatus('err', 'Vui lòng chọn 1 nhân viên để xuất.');
            return;
        }

        btnExportEmp && (btnExportEmp.disabled = true);
        setStatus('info', 'Đang xuất Excel (nhân viên đã chọn)...');

        try {
            const res = await window.bridge.invoke('PAYROLL_EXPORT_EXCEL', {
                dateFrom,
                dateTo,
                employeeId: employeeId,
                splitToSheetsByEmployee: true
            }, 60000);


            if (res?.data?.cancelled) {
                setStatus('info', 'Đã hủy xuất Excel.');
                return;
            }

            const path = res?.data?.path || res?.data?.Path || '';
            setStatus('ok', path ? `Xuất Excel thành công: ${path}` : 'Xuất Excel thành công.');
        } catch (e) {
            console.error('PAYROLL_EXPORT_EXCEL', e);
            setStatus('err', e?.message || 'Xuất Excel thất bại (xem console).');
        } finally {
            btnExportEmp && (btnExportEmp.disabled = false);
            updateExportEmpState();
        }
    }

    // events
    btnLoad?.addEventListener('click', (ev) => { ev.preventDefault(); loadPayroll('today'); });
    btnExport?.addEventListener('click', (ev) => { ev.preventDefault(); exportExcelAll(); });
    btnExportEmp?.addEventListener('click', (ev) => { ev.preventDefault(); exportExcelSelectedEmployee(); });
    txtSearch?.addEventListener('input', () => applyFilter());

    // ✅ Khi chọn ngày / nhân viên thì tự load theo khoảng đã chọn
    elFrom?.addEventListener('change', () => loadPayroll('range'));
    elTo?.addEventListener('change', () => loadPayroll('range'));
    elEmp?.addEventListener('change', () => { updateExportEmpState(); loadPayroll('range'); });

    setDefaults();
    updateExportEmpState();
    loadEmployees().finally(() => { updateExportEmpState(); loadPayroll('today'); });
};

//Multiplier
window.pageInit = window.pageInit || {};

// NOTE: page file is pages/pay_multipliers.html => key must be 'pay_multipliers'
// Keep backward-compat alias for the old typo 'pay_mutipliers'.
window.pageInit.pay_multipliers = window.pageInit.pay_mutipliers = function () {

    // ====== helpers ======
    const pad2 = (n) => String(n).padStart(2, '0');
    const ymd = (d) => `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
    const parseYmd = (s) => {
        if (!s) return null;
        const m = String(s).match(/^(\d{4})-(\d{2})-(\d{2})$/);
        if (!m) return null;
        const dt = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
        return isNaN(dt.getTime()) ? null : dt;
    };
    const startOfMonth = (d) => new Date(d.getFullYear(), d.getMonth(), 1);
    const endOfMonth = (d) => new Date(d.getFullYear(), d.getMonth() + 1, 0);
    const monthTitle = (d) => `Tháng ${d.getMonth() + 1}/${d.getFullYear()}`;
    const esc = (s) => String(s ?? '')
        .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;').replaceAll("'", "&#39;");

    // ====== DOM ======
    const calGrid = document.getElementById('calGrid');
    const monthName = document.getElementById('monthName');
    const btnPrev = document.getElementById('btnPrev');
    const btnNext = document.getElementById('btnNext');
    const btnToday = document.getElementById('btnToday');
    const btnReload = document.getElementById('btnReload');
    const btnClear = document.getElementById('btnClear');

    const pillMul = document.getElementById('pillMul');
    const pillDate = document.getElementById('pillDate');

    const mulDate = document.getElementById('mulDate');
    const mulValue = document.getElementById('mulValue');
    const mulNote = document.getElementById('mulNote');
    const btnSave = document.getElementById('btnSave');
    const btnDelete = document.getElementById('btnDelete');
    const statusEl = document.getElementById('status');

    const tblBody = document.getElementById('tblBody');
    const listHint = document.getElementById('listHint');

    // ====== state ======
    let viewMonth = startOfMonth(new Date());       // đang xem tháng nào
    let selectedDate = null;                        // yyyy-mm-dd
    let mapByDate = new Map();                      // yyyy-mm-dd -> {WorkDate, Multiplier, Note}

    function setStatus(type, msg) {
        statusEl.className = 'status' + (type ? (' ' + type) : '');
        statusEl.textContent = msg || '';
    }

    function setEditorFromDate(dateYmd) {
        selectedDate = dateYmd || null;
        mulDate.value = selectedDate || '';
        const item = selectedDate ? mapByDate.get(selectedDate) : null;

        if (item) {
            mulValue.value = String(Number(item.Multiplier ?? item.multiplier ?? 1));
            mulNote.value = item.Note ?? item.note ?? '';
            btnDelete.disabled = false;
        } else {
            // default
            mulValue.value = '2';
            mulNote.value = '';
            btnDelete.disabled = true;
        }

        btnSave.disabled = !selectedDate;
        pillDate.textContent = selectedDate || '—';
        pillMul.textContent = selectedDate ? (item ? Number(item.Multiplier ?? 1).toFixed(2) : Number(mulValue.value).toFixed(2)) : '—';
    }

    function buildCalendar() {
        const d = new Date(viewMonth.getFullYear(), viewMonth.getMonth(), 1);
        monthName.textContent = monthTitle(d);

        const first = startOfMonth(d);
        const last = endOfMonth(d);

        const startDow = first.getDay(); // 0..6 (CN..T7)
        const daysInMonth = last.getDate();

        // Build header
        const dows = ['CN', 'T2', 'T3', 'T4', 'T5', 'T6', 'T7'];
        let html = '';
        for (const w of dows) {
            html += `<div class="dow">${w}</div>`;
        }

        // Fill blanks before first
        const prevMonthLast = new Date(d.getFullYear(), d.getMonth(), 0);
        const prevDays = prevMonthLast.getDate();
        for (let i = 0; i < startDow; i++) {
            const dayNum = prevDays - startDow + 1 + i;
            html += `<div class="day muted" data-ymd="">
        <div class="dnum">${dayNum}</div>
        <div class="tag">—</div>
      </div>`;
        }

        // Days of month
        for (let day = 1; day <= daysInMonth; day++) {
            const dt = new Date(d.getFullYear(), d.getMonth(), day);
            const key = ymd(dt);
            const item = mapByDate.get(key);
            const has = !!item;
            const mul = has ? Number(item.Multiplier ?? item.multiplier ?? 1).toFixed(2) : '';
            const note = has ? (item.Note ?? item.note ?? '') : '';
            const cls = [
                'day',
                has ? 'has-mul' : '',
                (selectedDate === key) ? 'selected' : ''
            ].join(' ').trim();

            html += `<div class="${cls}" data-ymd="${esc(key)}">
        <div class="dnum">${day}</div>
        <div class="tag">${has ? `HS ${esc(mul)}${note ? ' • ' + esc(note) : ''}` : '—'}</div>
      </div>`;
        }

        // Fill blanks to complete grid to multiple of 7
        const totalCells = (7 + startDow + daysInMonth); // include dow header
        const cellsWithoutHeader = startDow + daysInMonth;
        const mod = cellsWithoutHeader % 7;
        const pad = mod === 0 ? 0 : (7 - mod);
        for (let i = 1; i <= pad; i++) {
            html += `<div class="day muted" data-ymd="">
        <div class="dnum">${i}</div>
        <div class="tag">—</div>
      </div>`;
        }

        calGrid.innerHTML = html;
    }

    function renderList() {
        // list within current month
        const first = startOfMonth(viewMonth);
        const last = endOfMonth(viewMonth);
        const from = ymd(first);
        const to = ymd(last);

        const items = [];
        for (const [k, v] of mapByDate.entries()) {
            if (k >= from && k <= to) items.push(v);
        }
        items.sort((a, b) => String(a.WorkDate).localeCompare(String(b.WorkDate)));

        listHint.textContent = `${items.length} ngày`;

        if (!items.length) {
            tblBody.innerHTML = `<tr><td colspan="4" class="sub" style="padding:12px 8px;">Chưa có dữ liệu trong tháng này.</td></tr>`;
            return;
        }

        tblBody.innerHTML = items.map(x => {
            const d = String(x.WorkDate ?? x.workDate ?? '').slice(0, 10);
            const m = Number(x.Multiplier ?? x.multiplier ?? 1).toFixed(2);
            const note = x.Note ?? x.note ?? '';
            return `<tr>
        <td>${esc(d)}</td>
        <td><span class="pill">${esc(m)}</span></td>
        <td>${esc(note)}</td>
        <td class="actions">
          <button class="btn ghost" data-edit="${esc(d)}">Sửa</button>
          <button class="btn danger" data-del="${esc(d)}">Xóa</button>
        </td>
      </tr>`;
        }).join('');
    }

    async function apiListMonth() {
        const first = startOfMonth(viewMonth);
        const last = endOfMonth(viewMonth);
        const dateFrom = ymd(first);
        const dateTo = ymd(last);

        setStatus('', 'Đang tải dữ liệu...');
        try {
            const res = await window.bridge.invoke('PAY_MULTIPLIER_LIST', { dateFrom, dateTo }, 15000);
            const rows = Array.isArray(res?.data) ? res.data : [];

            mapByDate.clear();
            for (const r of rows) {
                const k = String(r.WorkDate ?? r.workDate ?? '').slice(0, 10);
                if (!k) continue;
                mapByDate.set(k, r);
            }

            setStatus('ok', `Đã tải ${rows.length} ngày hệ số trong ${monthTitle(viewMonth)}.`);
        } catch (e) {
            console.error(e);
            setStatus('err', e?.message || 'Tải dữ liệu thất bại.');
            mapByDate.clear();
        }

        buildCalendar();
        renderList();
        // refresh editor if selected
        if (selectedDate) setEditorFromDate(selectedDate);
    }

    async function apiUpsert() {
        const workDate = String(mulDate.value || '').trim();
        const multiplier = Number(mulValue.value || 2);
        const note = String(mulNote.value || '').trim() || null;

        if (!workDate) { setStatus('err', 'Vui lòng chọn ngày.'); return; }
        if (!Number.isFinite(multiplier) || multiplier <= 0) { setStatus('err', 'Hệ số không hợp lệ.'); return; }

        btnSave.disabled = true;
        try {
            await window.bridge.invoke('PAY_MULTIPLIER_UPSERT', { workDate, multiplier, note }, 15000);
            setStatus('ok', `Đã lưu hệ số ${multiplier.toFixed(2)} cho ${workDate}.`);
            // reload month list to keep consistent
            await apiListMonth();
            setEditorFromDate(workDate);
        } catch (e) {
            console.error(e);
            setStatus('err', e?.message || 'Lưu thất bại.');
        } finally {
            btnSave.disabled = false;
        }
    }

    async function apiDelete(workDate) {
        if (!workDate) { setStatus('err', 'Chưa chọn ngày.'); return; }
        btnDelete.disabled = true;
        try {
            await window.bridge.invoke('PAY_MULTIPLIER_DELETE', { workDate }, 15000);
            setStatus('ok', `Đã xóa hệ số ngày ${workDate}.`);
            await apiListMonth();
            setEditorFromDate('');
        } catch (e) {
            console.error(e);
            setStatus('err', e?.message || 'Xóa thất bại.');
        } finally {
            // will be set by setEditorFromDate after reload
        }
    }

    // ====== events ======
    btnPrev.addEventListener('click', async () => {
        viewMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() - 1, 1);
        await apiListMonth();
    });
    btnNext.addEventListener('click', async () => {
        viewMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() + 1, 1);
        await apiListMonth();
    });
    btnToday.addEventListener('click', async () => {
        viewMonth = startOfMonth(new Date());
        await apiListMonth();
        setEditorFromDate(ymd(new Date()));
    });
    btnReload.addEventListener('click', async () => { await apiListMonth(); });

    btnClear.addEventListener('click', () => {
        setEditorFromDate('');
        buildCalendar();
    });

    mulDate.addEventListener('change', () => {
        const v = String(mulDate.value || '').trim();
        setEditorFromDate(v);
        buildCalendar();
    });
    mulValue.addEventListener('change', () => {
        if (selectedDate) {
            pillMul.textContent = Number(mulValue.value || 2).toFixed(2);
        }
    });

    btnSave.addEventListener('click', (e) => { e.preventDefault(); apiUpsert(); });
    btnDelete.addEventListener('click', (e) => { e.preventDefault(); apiDelete(selectedDate); });

    calGrid.addEventListener('click', (e) => {
        const box = e.target.closest('.day');
        if (!box) return;
        const key = box.getAttribute('data-ymd');
        if (!key) return;

        setEditorFromDate(key);
        buildCalendar();
    });

    tblBody.addEventListener('click', (e) => {
        const edit = e.target.closest('button[data-edit]');
        const del = e.target.closest('button[data-del]');
        if (edit) {
            const d = edit.getAttribute('data-edit');
            setEditorFromDate(d);
            buildCalendar();
            return;
        }
        if (del) {
            const d = del.getAttribute('data-del');
            apiDelete(d);
        }
    });

    // ===== init =====
    setEditorFromDate('');
    apiListMonth();
};

// ===== Admin PIN settings page init =====
window.pageInit.pin_settings = function () {
    const statusEl = document.getElementById("pin-status");
    const inpCurrent = document.getElementById("pin-current");
    const inpNew = document.getElementById("pin-new");
    const inpConfirm = document.getElementById("pin-confirm");

    const btnChange = document.getElementById("btn-change-pin");
    const btnResetDefault = document.getElementById("btn-reset-default");
    const btnCopy = document.getElementById("btn-copy-script");
    const pre = document.getElementById("reset-script");

    const inpRecovery = document.getElementById("pin-recovery");
    const inpRecoveryNew = document.getElementById("pin-recovery-new");
    const btnResetRecovery = document.getElementById("btn-reset-recovery");

    if (!globalThis.bridge?.invoke) return;

    function show(type, msg) {
        if (!statusEl) return;
        statusEl.classList.remove("hidden");
        statusEl.className =
            "mb-4 px-4 py-3 rounded-xl border text-sm font-semibold " +
            (type === "ok"
                ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                : type === "err"
                    ? "bg-red-50 text-red-700 border-red-200"
                    : "bg-slate-50 text-slate-700 border-slate-200");
        statusEl.textContent = msg || "";
    }

    function buildResetScript(dbName, defaultPin) {
        const safeDb = dbName ? `[${dbName}]` : "[TEN_DATABASE_CUA_BAN]";
        const pin = String(defaultPin || "260302");
        return `-- Reset mã PIN quản lý cho app ChamCong\n-- 1) Đổi ${safeDb} nếu DB của bạn khác\n\nUSE ${safeDb};\nGO\n\nIF OBJECT_ID('dbo.AppAdminPin','U') IS NULL\nBEGIN\n    CREATE TABLE dbo.AppAdminPin(\n        Id INT NOT NULL CONSTRAINT PK_AppAdminPin PRIMARY KEY,\n        PinHash VARBINARY(32) NOT NULL,\n        PinSalt VARBINARY(16) NOT NULL,\n        UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_AppAdminPin_UpdatedAt DEFAULT SYSDATETIME()\n    );\nEND\nGO\n\nDECLARE @Pin NVARCHAR(50) = N'${pin}';\nDECLARE @Salt VARBINARY(16) = CRYPT_GEN_RANDOM(16);\nDECLARE @Hash VARBINARY(32) = HASHBYTES('SHA2_256', @Salt + CONVERT(VARBINARY(MAX), @Pin));\n\nMERGE dbo.AppAdminPin AS t\nUSING (SELECT 1 AS Id) s\nON t.Id = s.Id\nWHEN MATCHED THEN\n  UPDATE SET PinHash=@Hash, PinSalt=@Salt, UpdatedAt=SYSDATETIME()\nWHEN NOT MATCHED THEN\n  INSERT (Id, PinHash, PinSalt, UpdatedAt) VALUES (1, @Hash, @Salt, SYSDATETIME());\nGO\n\nSELECT 'DONE' AS Result, '${pin}' AS DefaultPin;`;
    }

    async function loadStatus() {
        try {
            const res = await globalThis.bridge.invoke("ADMIN_PIN_STATUS", null);
            const dbName = res?.data?.dbName || "";
            const defaultPin = res?.data?.defaultPin || "260302";
            if (pre) pre.textContent = buildResetScript(dbName, defaultPin);
            show("info", `PIN mặc định hiện tại: ${defaultPin}. (Bạn nên đổi ngay.)`);
        } catch (e) {
            if (pre) pre.textContent = buildResetScript("", "260302");
            show("err", e?.message || "Không thể tải trạng thái PIN.");
        }
    }

    btnChange?.addEventListener("click", async () => {
        const currentPin = String(inpCurrent?.value || "").trim();
        const newPin = String(inpNew?.value || "").trim();
        const confirm = String(inpConfirm?.value || "").trim();

        if (!currentPin) return show("err", "Vui lòng nhập PIN hiện tại.");
        if (!newPin || newPin.length < 4) return show("err", "PIN mới phải có ít nhất 4 ký tự.");
        if (newPin !== confirm) return show("err", "PIN mới và nhập lại không khớp.");

        try {
            await globalThis.bridge.invoke("ADMIN_PIN_CHANGE", { currentPin, newPin });
            inpCurrent.value = "";
            inpNew.value = "";
            inpConfirm.value = "";
            show("ok", "Đã đổi PIN thành công.");
            await loadStatus();
        } catch (e) {
            show("err", e?.message || "Đổi PIN thất bại.");
        }
    });

    btnResetDefault?.addEventListener("click", async () => {
        const ok = confirm("Bạn muốn reset PIN về mặc định? (Sau đó nhớ đổi ngay)");
        if (!ok) return;
        try {
            await globalThis.bridge.invoke("ADMIN_PIN_RESET_DEFAULT", null);
            show("ok", "Đã reset PIN về mặc định.");
            await loadStatus();
        } catch (e) {
            show("err", e?.message || "Reset thất bại.");
        }
    });

    btnCopy?.addEventListener("click", async () => {
        try {
            const text = pre?.textContent || "";
            await navigator.clipboard.writeText(text);
            show("ok", "Đã copy script.");
        } catch {
            // fallback
            try {
                const r = document.createRange();
                r.selectNodeContents(pre);
                const sel = window.getSelection();
                sel.removeAllRanges();
                sel.addRange(r);
                document.execCommand("copy");
                sel.removeAllRanges();
                show("ok", "Đã copy script.");
            } catch (e) {
                show("err", "Không thể copy. Bạn hãy bôi đen và copy thủ công.");
            }
        }
    });

    btnResetRecovery?.addEventListener("click", async () => {
        const recoveryCode = String(inpRecovery?.value || "").trim();
        const newPin = String(inpRecoveryNew?.value || "").trim();
        if (!recoveryCode) return show("err", "Vui lòng nhập mã khôi phục.");
        if (!newPin || newPin.length < 4) return show("err", "PIN mới phải có ít nhất 4 ký tự.");

        try {
            await globalThis.bridge.invoke("ADMIN_PIN_RESET_RECOVERY", { recoveryCode, newPin });
            inpRecovery.value = "";
            inpRecoveryNew.value = "";
            show("ok", "Đã reset PIN thành công.");
            await loadStatus();
        } catch (e) {
            show("err", e?.message || "Reset thất bại.");
        }
    });

    loadStatus();
};

// ===== Settings: Manual API endpoint =====
// Trang: pages/settings_connection.html
window.pageInit.settings_connection = function () {
    const statusEl = document.getElementById("conn-status");
    const inpHost = document.getElementById("conn-host");
    const inpPort = document.getElementById("conn-port");
    const inpPreview = document.getElementById("conn-preview");
    const btnTest = document.getElementById("btn-conn-test");
    const btnSave = document.getElementById("btn-conn-save");
    const btnReset = document.getElementById("btn-conn-reset");

    if (!globalThis.bridge?.invoke) return;

    function getScheme() {
        return document.querySelector('input[name="conn-scheme"]:checked')?.value || "http";
    }

    function show(type, msg) {
        if (!statusEl) return;
        statusEl.classList.remove("hidden");
        statusEl.className =
            "mb-4 px-4 py-3 rounded-xl border text-sm font-semibold " +
            (type === "ok"
                ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                : type === "err"
                    ? "bg-red-50 text-red-700 border-red-200"
                    : "bg-slate-50 text-slate-700 border-slate-200");
        statusEl.textContent = msg || "";
    }

    function updatePreview() {
        const host = String(inpHost?.value || "").trim();
        const port = String(inpPort?.value || "").trim();
        const scheme = getScheme();
        if (inpPreview) inpPreview.value = host ? `${scheme}://${host}:${port || "5000"}` : "";
    }

    function validate() {
        const host = String(inpHost?.value || "").trim();
        const port = Number(inpPort?.value || 0);
        if (!host) return "Vui lòng nhập IP/Host.";
        if (!Number.isInteger(port) || port < 1 || port > 65535) return "Port không hợp lệ.";
        return "";
    }

    async function load() {
        try {
            const res = await globalThis.bridge.invoke("SETTINGS_GET", null);
            const api = res?.data?.api || res?.data?.Api || {};

            inpHost.value = api.host || api.Host || "localhost";
            inpPort.value = api.port || api.Port || 5000;

            const scheme = String(api.scheme || api.Scheme || "http").toLowerCase();
            const radio = document.querySelector(`input[name="conn-scheme"][value="${scheme}"]`);
            radio?.click();

            updatePreview();
            show("info", "Đã tải cấu hình hiện tại.");
        } catch (e) {
            console.error(e);
            updatePreview();
            show("err", e?.message || "Không tải được cấu hình.");
        }
    }

    btnTest?.addEventListener("click", async () => {
        const err = validate();
        if (err) return show("err", err);

        show("info", "Đang kiểm tra kết nối...");
        try {
            const payload = { scheme: getScheme(), host: String(inpHost.value).trim(), port: Number(inpPort.value) };
            const res = await globalThis.bridge.invoke("API_TEST", payload, 15000);
            show("ok", res?.message || "Kết nối OK");
        } catch (e) {
            show("err", e?.message || "Không kết nối được.");
        }
    });

    btnSave?.addEventListener("click", async () => {
        const err = validate();
        if (err) return show("err", err);

        show("info", "Đang lưu...");
        try {
            const payload = {
                api: { scheme: getScheme(), host: String(inpHost.value).trim(), port: Number(inpPort.value) },
                applyToBaseUrl: true,
                applyToPublicBaseUrl: true
            };
            const res = await globalThis.bridge.invoke("SETTINGS_SAVE", payload, 15000);
            show("ok", res?.message || "Đã lưu.");
        } catch (e) {
            show("err", e?.message || "Lưu thất bại.");
        }
    });

    btnReset?.addEventListener("click", () => {
        inpHost.value = "localhost";
        inpPort.value = 5000;
        document.querySelector('input[name="conn-scheme"][value="http"]')?.click();
        updatePreview();
        show("info", "Đã đặt về mặc định: http://localhost:5000");
    });

    inpHost?.addEventListener("input", updatePreview);
    inpPort?.addEventListener("input", updatePreview);
    document.querySelectorAll('input[name="conn-scheme"]').forEach(r => r.addEventListener("change", updatePreview));

    load();
};

//NhapPin
let __pinModalResolve = null;

function openPinModal({
    subtitle = "Mục này cần mã PIN của chủ quán.",
    onConfirm = null,      // (pin) => Promise<boolean> | boolean
    onCancel = null,
} = {}) {
    const modal = document.getElementById("pin-modal");
    const backdrop = document.getElementById("pin-backdrop");
    const closeBtn = document.getElementById("pin-close");
    const cancelBtn = document.getElementById("pin-cancel");
    const confirmBtn = document.getElementById("pin-confirm");
    const input = document.getElementById("pin-input");
    const subtitleEl = document.getElementById("pin-modal-subtitle");
    const err = document.getElementById("pin-error");

    if (!modal || !backdrop || !closeBtn || !cancelBtn || !confirmBtn || !input || !subtitleEl || !err) {
        console.error("[PIN] Missing modal elements in index.html");
        return Promise.resolve(null);
    }

    // ✅ FORCE overlay full-screen + center (không phụ thuộc Tailwind)
    modal.style.position = "fixed";
    modal.style.left = "0";
    modal.style.top = "0";
    modal.style.right = "0";
    modal.style.bottom = "0";
    modal.style.width = "100vw";
    modal.style.height = "100vh";
    modal.style.zIndex = "999999";
    modal.style.display = "block";

    backdrop.style.position = "absolute";
    backdrop.style.left = "0";
    backdrop.style.top = "0";
    backdrop.style.right = "0";
    backdrop.style.bottom = "0";
    backdrop.style.background = "rgba(0,0,0,0.4)";

    // wrapper = div bọc dialog (ngay sau backdrop)
    const wrapper = backdrop.nextElementSibling;
    if (wrapper) {
        wrapper.style.position = "relative";
        wrapper.style.width = "100%";
        wrapper.style.height = "100%";
        wrapper.style.display = "flex";
        wrapper.style.alignItems = "center";
        wrapper.style.justifyContent = "center";
        wrapper.style.padding = "16px";
    }

    // dialog = khung trắng chứa nội dung
    const dialog = wrapper?.firstElementChild;
    if (dialog) {
        // ✅ GỌN lại (nhỏ, gần vuông)
        dialog.style.width = "360px";
        dialog.style.maxWidth = "92vw";
        dialog.style.borderRadius = "16px";
    }

    // Ngăn scroll nền khi modal mở
    document.body.classList.add("overflow-hidden");

    // Reset UI
    subtitleEl.textContent = subtitle;
    err.classList.add("hidden");
    err.textContent = "";
    input.value = "";
    confirmBtn.disabled = false;

    // Thu gọn padding + font (để không bị dài)
    try {
        const header = dialog?.children?.[0];
        const body = dialog?.children?.[1];

        if (header) header.style.padding = "14px 16px";
        if (body) body.style.padding = "14px 16px";

        const title = dialog?.querySelector("h3");
        if (title) {
            title.style.fontSize = "16px";
            title.style.lineHeight = "20px";
        }
        subtitleEl.style.fontSize = "12px";
        subtitleEl.style.marginTop = "4px";

        input.style.height = "40px";
        input.style.borderRadius = "10px";
        input.style.fontSize = "14px";
        input.style.padding = "0 12px";

        cancelBtn.style.height = "34px";
        cancelBtn.style.padding = "0 12px";
        cancelBtn.style.fontSize = "13px";
        cancelBtn.style.borderRadius = "10px";

        confirmBtn.style.height = "34px";
        confirmBtn.style.padding = "0 12px";
        confirmBtn.style.fontSize = "13px";
        confirmBtn.style.borderRadius = "10px";

        const btnRow = dialog?.querySelector(".mt-6") || dialog?.querySelector("[class*='mt-6']");
        if (btnRow) btnRow.style.marginTop = "12px";

        err.style.fontSize = "12px";
        err.style.marginTop = "8px";
    } catch { /* ignore */ }

    function showError(msg) {
        err.textContent = msg || "Có lỗi.";
        err.classList.remove("hidden");
    }

    function close() {
        modal.classList.add("hidden");
        modal.style.display = "none";
        document.body.classList.remove("overflow-hidden");
        document.removeEventListener("keydown", onKeyDown);
    }

    async function handleConfirm() {
        const pin = (input.value || "").trim();
        if (!pin) return showError("Vui lòng nhập mã PIN.");

        try {
            confirmBtn.disabled = true;

            if (typeof onConfirm === "function") {
                const ok = await onConfirm(pin);
                if (!ok) {
                    confirmBtn.disabled = false;
                    return showError("PIN không đúng. Vui lòng thử lại.");
                }
            }

            close();
            if (__pinModalResolve) __pinModalResolve(pin);
        } catch (e) {
            confirmBtn.disabled = false;
            showError("Có lỗi khi xác thực PIN.");
        }
    }

    function handleCancel() {
        close();
        if (typeof onCancel === "function") onCancel();
        if (__pinModalResolve) __pinModalResolve(null);
    }

    function onKeyDown(e) {
        if (e.key === "Escape") handleCancel();
        if (e.key === "Enter") handleConfirm();
    }

    // bind events
    backdrop.onclick = handleCancel;
    closeBtn.onclick = handleCancel;
    cancelBtn.onclick = handleCancel;
    confirmBtn.onclick = handleConfirm;

    // show
    modal.classList.remove("hidden");
    modal.style.display = "block";
    document.addEventListener("keydown", onKeyDown);

    setTimeout(() => input.focus(), 0);

    return new Promise((resolve) => {
        __pinModalResolve = resolve;
    });
}
