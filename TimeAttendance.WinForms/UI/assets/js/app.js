// Simple router for WebView2 (offline)
const contentEl = document.getElementById("app-content");
const titleEl = document.getElementById("page-title");

function setTitle(text) {
    if (titleEl) titleEl.textContent = text || "";
}

function setActive(navEl) {
    document.querySelectorAll(".nav-item[data-page]").forEach(el => {
        el.classList.remove("bg-primary", "text-[#0d1a1b]");
        // khôi phục hover
        el.classList.add("hover:bg-[#e7f2f3]", "dark:hover:bg-[#1a3538]");
    });

    navEl.classList.add("bg-primary", "text-[#0d1a1b]");
    navEl.classList.remove("hover:bg-[#e7f2f3]", "dark:hover:bg-[#1a3538]");
}

async function loadPage(page) {
    const res = await fetch(page, { cache: "no-store" });
    if (!res.ok) throw new Error(`Cannot load: ${page} (${res.status})`);
    contentEl.innerHTML = await res.text();
}

async function navigate(navEl) {
    const page = navEl.dataset.page;
    const title = navEl.dataset.title || "Page";

    try {
        await loadPage(page);
        setActive(navEl);
        setTitle(title);
    } catch (err) {
        console.error(err);
        contentEl.innerHTML = `
      <div class="p-6 rounded-xl border border-red-200 bg-red-50 text-red-700">
        <div class="font-bold mb-1">Không thể tải trang</div>
        <div class="text-sm">${page}</div>
      </div>
    `;
    }
}

(function () {
    const pending = new Map();

    function uuid() {
        return crypto.randomUUID ? crypto.randomUUID() : (Date.now() + Math.random()).toString(16);
    }

    function post(req) {
        return new Promise((resolve, reject) => {
            pending.set(req.id, { resolve, reject });
            chrome.webview.postMessage(req);
        });
    }

    chrome.webview.addEventListener("message", (e) => {
        const res = e.data;
        const p = pending.get(res.id);
        if (!p) return;
        pending.delete(res.id);
        if (res.ok) p.resolve(res);
        else p.reject(res);
    });

    window.bridge = {
        invoke: (type, payload = {}) => post({ id: uuid(), type, payload })
    };
})();



// Click handler (sidebar)
document.addEventListener("click", (e) => {
    const navEl = e.target.closest(".nav-item[data-page]");
    if (!navEl) return;
    navigate(navEl);
});

// Default page
document.addEventListener("DOMContentLoaded", () => {
    const defaultEl =
        document.querySelector('.nav-item[data-page="pages/dashboard.html"]') ||
        document.querySelector(".nav-item[data-page]");

    if (defaultEl) navigate(defaultEl);
});

document.getElementById("btn-checkin")?.addEventListener("click", async () => {
    const employeeCode = document.getElementById("employee-code").value.trim();
    const pin = [...document.querySelectorAll("input[maxlength='1']")].map(i => i.value).join("");
    try {
        const res = await window.bridge.invoke("CHECK_IN", { employeeCode, pin });
        alert(res.message);
    } catch (err) {
        alert(err.message || "Check-in lỗi");
    }
});

document.getElementById("btn-checkout")?.addEventListener("click", async () => {
    const employeeCode = document.getElementById("employee-code").value.trim();
    const pin = [...document.querySelectorAll("input[maxlength='1']")].map(i => i.value).join("");
    try {
        const res = await window.bridge.invoke("CHECK_OUT", { employeeCode, pin });
        alert(res.message);
    } catch (err) {
        alert(err.message || "Check-out lỗi");
    }
});

window.pageInit = window.pageInit || {};

window.pageInit.schedule = function () {
    const elWeekLabel = document.getElementById("sch-week-label");
    const elEmp = document.getElementById("sch-emp");
    const elReload = document.getElementById("sch-reload");
    const elStatus = document.getElementById("sch-status");
    const head = document.getElementById("sch-grid-head");
    const body = document.getElementById("sch-grid-body");

    // modal
    const modal = document.getElementById("sch-modal");
    const modalClose = document.getElementById("sch-modal-close");
    const modalCancel = document.getElementById("sch-cancel");
    const modalSave = document.getElementById("sch-save");
    const modalDel = document.getElementById("sch-del");
    const modalTitle = document.getElementById("sch-modal-title");
    const modalSub = document.getElementById("sch-modal-sub");
    const modalErr = document.getElementById("sch-modal-error");
    const selShift = document.getElementById("sch-shift");
    const inpNote = document.getElementById("sch-note");

    if (!elEmp || !head || !body) return;

    const state = {
        weekStart: null, // Date
        days: [],        // [{date: Date, key:'YYYY-MM-DD'}]
        shifts: [],
        schedulesByDate: new Map(), // key -> schedule
        pickDateKey: null
    };

    const pad2 = (n) => String(n).padStart(2, "0");
    const toKey = (d) => `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;

    function setStatus(type, msg) {
        if (!elStatus) return;
        if (!msg) { elStatus.classList.add("hidden"); elStatus.textContent = ""; return; }
        elStatus.classList.remove("hidden");
        elStatus.className = "mt-4 p-4 rounded-xl border text-sm font-semibold " +
            (type === "ok" ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                : type === "err" ? "bg-red-50 text-red-700 border-red-200"
                    : "bg-slate-50 text-slate-700 border-slate-200");
        elStatus.textContent = msg;
    }

    // Tuần kế tiếp: lấy Thứ 2 của tuần sau
    function getNextWeekStart() {
        const now = new Date();
        const day = now.getDay(); // 0 CN, 1 T2...6 T7
        const diffToMonThisWeek = (day === 0 ? -6 : 1 - day);
        const monThisWeek = new Date(now);
        monThisWeek.setDate(now.getDate() + diffToMonThisWeek);
        monThisWeek.setHours(0, 0, 0, 0);
        const monNextWeek = new Date(monThisWeek);
        monNextWeek.setDate(monThisWeek.getDate() + 7);
        return monNextWeek;
    }

    function buildWeek(weekStart) {
        state.weekStart = weekStart;
        state.days = [];
        for (let i = 0; i < 7; i++) {
            const d = new Date(weekStart);
            d.setDate(weekStart.getDate() + i);
            state.days.push({ date: d, key: toKey(d) });
        }

        const end = new Date(weekStart);
        end.setDate(weekStart.getDate() + 6);

        elWeekLabel.textContent =
            `Tuần: ${pad2(weekStart.getDate())}/${pad2(weekStart.getMonth() + 1)}/${weekStart.getFullYear()}`
            + ` - ${pad2(end.getDate())}/${pad2(end.getMonth() + 1)}/${end.getFullYear()}`;

        renderGrid();
    }

    function renderGrid() {
        const dayNames = ["Th 2", "Th 3", "Th 4", "Th 5", "Th 6", "Th 7", "CN"];

        head.innerHTML = state.days.map((x, idx) => {
            const d = x.date;
            return `
        <div class="px-4 py-3 font-extrabold border-b border-[#cfe5e7] bg-slate-50">
          <div>${dayNames[idx]}</div>
          <div class="text-sm text-slate-500 font-bold">${pad2(d.getDate())}/${pad2(d.getMonth() + 1)}</div>
        </div>
      `;
        }).join("");

        body.innerHTML = state.days.map((x) => {
            const sc = state.schedulesByDate.get(x.key);
            if (!sc) {
                return `
          <button data-date="${x.key}" class="text-left px-4 py-5 min-h-[120px] border-b border-[#cfe5e7] hover:bg-[#e7f2f3]">
            <div class="text-sm font-extrabold text-slate-400">Chưa xếp ca</div>
            <div class="text-xs text-slate-400 mt-1">Bấm để gán ca</div>
          </button>
        `;
            }
            return `
        <button data-date="${x.key}" class="text-left px-4 py-5 min-h-[120px] border-b border-[#cfe5e7] hover:bg-[#e7f2f3]">
          <div class="inline-flex items-center gap-2">
            <span class="px-2 py-1 rounded-lg text-xs font-extrabold border border-emerald-200 bg-emerald-50 text-emerald-700">
              ${sc.shiftCode || "CA"}
            </span>
            <span class="text-sm font-extrabold">${sc.shiftName || ""}</span>
          </div>
          <div class="text-xs text-slate-500 mt-2">
            ${String(sc.startTime || "").slice(0, 5)} - ${String(sc.endTime || "").slice(0, 5)}
          </div>
          ${sc.note ? `<div class="text-xs text-slate-600 mt-2">${sc.note}</div>` : ""}
        </button>
      `;
        }).join("");
    }
    /*Calendar*/
    function openModal(dateKey) {
        state.pickDateKey = dateKey;
        const d = state.days.find(x => x.key === dateKey)?.date;
        const sc = state.schedulesByDate.get(dateKey);

        if (modalErr) { modalErr.classList.add("hidden"); modalErr.textContent = ""; }

        modalTitle.textContent = "Gán ca";
        modalSub.textContent = d ? `Ngày ${pad2(d.getDate())}/${pad2(d.getMonth() + 1)}/${d.getFullYear()}` : dateKey;

        // fill shift options
        selShift.innerHTML = state.shifts.map(s =>
            `<option value="${s.shiftId}">${s.shiftCode} - ${s.shiftName} (${String(s.startTime).slice(0, 5)}-${String(s.endTime).slice(0, 5)})</option>`
        ).join("");

        if (sc) {
            selShift.value = String(sc.shiftId);
            inpNote.value = sc.note || "";
            modalDel.disabled = false;
        } else {
            selShift.selectedIndex = 0;
            inpNote.value = "";
            modalDel.disabled = true;
        }

        modal.classList.remove("hidden");
    }

    function closeModal() {
        modal.classList.add("hidden");
        state.pickDateKey = null;
    }

    function showModalError(msg) {
        if (!modalErr) return;
        modalErr.textContent = msg;
        modalErr.classList.remove("hidden");
    }

    async function loadEmployees() {
        const res = await window.bridge.invoke("EMPLOYEE_LIST", { includeInactive: false });
        const list = Array.isArray(res.data) ? res.data : [];
        elEmp.innerHTML = list.map(e => {
            const id = e.employeeId ?? e.EmployeeId;
            const name = e.fullName ?? e.FullName;
            const code = e.employeeCode ?? e.EmployeeCode;
            return `<option value="${id}">${code} - ${name}</option>`;
        }).join("");
    }

    async function loadShifts() {
        const res = await window.bridge.invoke("SHIFT_LIST", { includeInactive: false });
        const list = Array.isArray(res.data) ? res.data : [];
        state.shifts = list.map(x => ({
            shiftId: x.shiftId ?? x.ShiftId,
            shiftCode: x.shiftCode ?? x.ShiftCode,
            shiftName: x.shiftName ?? x.ShiftName,
            startTime: x.startTime ?? x.StartTime,
            endTime: x.endTime ?? x.EndTime
        }));
    }

    async function loadSchedules() {
        const employeeId = Number(elEmp.value);
        const dateFrom = state.days[0].key;
        const dateTo = state.days[6].key;

        const res = await window.bridge.invoke("SCHEDULE_LIST", { employeeId, dateFrom, dateTo });
        const list = Array.isArray(res.data) ? res.data : [];

        state.schedulesByDate = new Map();
        for (const x of list) {
            const workDate = (x.workDate ?? x.WorkDate);
            const key = typeof workDate === "string" ? workDate.slice(0, 10) : String(workDate);
            state.schedulesByDate.set(key, {
                scheduleId: x.scheduleId ?? x.ScheduleId,
                employeeId: x.employeeId ?? x.EmployeeId,
                workDate: key,
                shiftId: x.shiftId ?? x.ShiftId,
                shiftCode: x.shiftCode ?? x.ShiftCode,
                shiftName: x.shiftName ?? x.ShiftName,
                startTime: x.startTime ?? x.StartTime,
                endTime: x.endTime ?? x.EndTime,
                note: x.note ?? x.Note
            });
        }
        renderGrid();
    }

    async function refresh() {
        try {
            setStatus("info", "Đang tải lịch tuần...");
            await loadShifts();
            await loadSchedules();
            setStatus(null, "");
        } catch (e) {
            console.error(e);
            setStatus("err", e?.message || "Tải lịch thất bại.");
        }
    }

    async function saveSchedule() {
        const employeeId = Number(elEmp.value);
        const workDate = state.pickDateKey;
        const shiftId = Number(selShift.value);
        const note = (inpNote.value || "").trim() || null;

        if (!employeeId) return showModalError("Chưa chọn nhân viên.");
        if (!workDate) return showModalError("Chưa chọn ngày.");
        if (!shiftId) return showModalError("Chưa chọn ca.");

        try {
            await window.bridge.invoke("SCHEDULE_UPSERT", { employeeId, workDate, shiftId, note }, 12000);
            await loadSchedules();
            closeModal();
            setStatus("ok", "Đã lưu lịch.");
            setTimeout(() => setStatus(null, ""), 1500);
        } catch (e) {
            showModalError(e?.message || "Lưu lịch thất bại.");
        }
    }

    async function deleteSchedule() {
        const employeeId = Number(elEmp.value);
        const workDate = state.pickDateKey;
        if (!employeeId || !workDate) return;

        if (!confirm("Xóa lịch ngày này?")) return;

        try {
            await window.bridge.invoke("SCHEDULE_DELETE", { employeeId, workDate }, 12000);
            await loadSchedules();
            closeModal();
            setStatus("ok", "Đã xóa lịch.");
            setTimeout(() => setStatus(null, ""), 1500);
        } catch (e) {
            showModalError(e?.message || "Xóa lịch thất bại.");
        }
    }

    // events
    elReload.addEventListener("click", refresh);
    elEmp.addEventListener("change", refresh);

    body.addEventListener("click", (ev) => {
        const btn = ev.target.closest("button[data-date]");
        if (!btn) return;
        openModal(btn.dataset.date);
    });

    modalClose.addEventListener("click", closeModal);
    modalCancel.addEventListener("click", closeModal);
    modalSave.addEventListener("click", saveSchedule);
    modalDel.addEventListener("click", deleteSchedule);
    modal.addEventListener("click", (ev) => {
        if (ev.target?.dataset?.schClose === "1") closeModal();
    });
    document.addEventListener("keydown", (ev) => {
        if (ev.key === "Escape" && !modal.classList.contains("hidden")) closeModal();
    });

    // init
    (async function init() {
        buildWeek(getNextWeekStart());
        await loadEmployees();
        await refresh();
    })();
};
