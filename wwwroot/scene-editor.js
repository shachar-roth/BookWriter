function reportClientDiagnostic(level, message, stack = "") {
    const payload = {
        level,
        message: String(message ?? "").slice(0, 2000),
        stack: String(stack ?? "").slice(0, 8000),
        page: `${window.location.pathname}${window.location.search}`.slice(0, 500),
        browser: navigator.userAgent.slice(0, 1000),
        viewportWidth: window.innerWidth,
        viewportHeight: window.innerHeight
    };
    fetch("/diagnostics/client-log", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
        keepalive: true
    }).catch(() => {});
}

window.addEventListener("error", event => {
    const location = event.filename ? `${event.filename}:${event.lineno}:${event.colno}` : "";
    reportClientDiagnostic("error", `${event.message || "Unhandled browser error"} ${location}`.trim(), event.error?.stack || "");
});

window.addEventListener("unhandledrejection", event => {
    const reason = event.reason;
    reportClientDiagnostic("error", reason?.message || String(reason || "Unhandled promise rejection"), reason?.stack || "");
});

window.sceneEditor = {
    dotNet: null,
    projectKey: "default",
    saveTimers: new Map(),
    scrollTrackingStarted: false,
    textareaResizeObserver: null,
    lastActiveChapter: null,
    lastActiveScene: null,
    lastNotifiedScene: null,
    preferences: null,
    browserSessionReported: false,

    bind(dotNetReference, projectKey) {
        this.dotNet = dotNetReference;
        this.projectKey = projectKey || "default";
        if (!this.browserSessionReported) {
            this.browserSessionReported = true;
            reportClientDiagnostic("info", "Editor browser session started");
        }

        this.preferences ??= loadEditorPreferences();
        applyEditorPreferences(this.preferences, false);

        document.querySelectorAll(".scene-content").forEach(textarea => {
            if (sizedTextareaValues.get(textarea) !== textarea.value) {
                resizeTextarea(textarea);
            }
            if (textarea.dataset.sceneEditorBound === "true") {
                return;
            }

            textarea.dataset.sceneEditorBound = "true";
            textarea.dataset.lastSaved = textarea.value;
            const draft = localStorage.getItem(draftKey(textarea.dataset.sceneTextarea));
            if (draft !== null && draft !== textarea.value) {
                textarea.value = draft;
                resizeTextarea(textarea);
                queueSave(textarea, 50);
            }

            textarea.addEventListener("input", handleSceneInput);
            textarea.addEventListener("blur", () => flushTextarea(textarea));
            textarea.addEventListener("keydown", handleSceneKeyDown);
            textarea.addEventListener("focus", handleSceneFocus);
            observeTextareaResize(textarea);
        });

        bindEditorTools();
        enableSceneMenuDrag(dotNetReference);
        bindScrollTracking();
        scheduleViewportUpdate();
    },

    async flushAll() {
        const textareas = Array.from(document.querySelectorAll(".scene-content"));
        await Promise.all(textareas.map(flushTextarea));
    },

    async getSceneValue(sceneId) {
        const textarea = document.querySelector(`[data-scene-textarea="${sceneId}"]`);
        if (!textarea) return null;
        await flushTextarea(textarea);
        return textarea.value;
    },

    getSelectionStart(sceneId) {
        return document.querySelector(`[data-scene-textarea="${sceneId}"]`)?.selectionStart ?? 0;
    },

    setSceneReadOnly(sceneId, isReadOnly) {
        const textarea = document.querySelector(`[data-scene-textarea="${sceneId}"]`);
        if (textarea) textarea.readOnly = Boolean(isReadOnly);
    },

    markSaved(sceneId, content) {
        const textarea = document.querySelector(`[data-scene-textarea="${sceneId}"]`);
        if (textarea) {
            textarea.dataset.lastSaved = content;
        }
        if (localStorage.getItem(draftKey(sceneId)) === content) {
            localStorage.removeItem(draftKey(sceneId));
        }
        setSaveIndicator("saved");
    },

    confirmDelete() {
        return window.confirm("למחוק את הסצנה?");
    },

    promptChapterName(currentName) {
        return window.prompt("שם הפרק", currentName ?? "");
    },

    scrollToScene(sceneId) {
        markActiveScene(sceneId, false);
        const target = document.getElementById(`scene-${sceneId}`);
        target?.scrollIntoView({ block: "start", behavior: "auto" });
        scheduleViewportUpdate();
        document.querySelector(".story-sidebar")?.classList.remove("is-mobile-open");
    },

    toggleMobileSidebar() {
        document.querySelector(".story-sidebar")?.classList.toggle("is-mobile-open");
    },

    closeMobileSidebar() {
        document.querySelector(".story-sidebar")?.classList.remove("is-mobile-open");
    },

    getStatistics() {
        return calculateManuscriptStatistics();
    }
};

let suppressNextSceneClick = false;
let scenePointerDrag = null;
let manuscriptStatsFrame = null;
let viewportUpdateFrame = null;
const sizedTextareaValues = new WeakMap();
const sizedTextareaWidths = new WeakMap();

const editorPreferencesKey = "israeli-author-studio:editor-preferences";
const editorFontFamilies = {
    system: "Arial, 'Helvetica Neue', sans-serif",
    arial: "Arial, 'Helvetica Neue', sans-serif",
    david: "David, 'Noto Serif Hebrew', serif",
    frank: "'Frank Ruhl Libre', 'FrankRuehl', 'Noto Serif Hebrew', serif",
    serif: "Georgia, 'Times New Roman', 'Noto Serif Hebrew', serif"
};

document.addEventListener("input", event => {
    if (event.target instanceof HTMLTextAreaElement && event.target.classList.contains("scene-content")) {
        queueManuscriptStats();
    }
});

function draftKey(sceneId) {
    return `israeli-author-studio:${window.sceneEditor.projectKey}:${sceneId}`;
}

function handleSceneInput(event) {
    const textarea = event.currentTarget;
    resizeTextarea(textarea);
    updateSceneNavLabel(textarea);
    localStorage.setItem(draftKey(textarea.dataset.sceneTextarea), textarea.value);
    setSaveIndicator("saving");
    queueSave(textarea, 800);
    queueManuscriptStats();
}

function handleSceneFocus(event) {
    const sceneId = event.currentTarget.dataset.sceneTextarea;
    if (sceneId) markActiveScene(sceneId, true);
}

function bindEditorTools() {
    const panel = document.querySelector("[data-editor-tools]");
    if (!panel) return;

    const preferences = window.sceneEditor.preferences ?? loadEditorPreferences();
    window.sceneEditor.preferences = preferences;
    applyEditorPreferences(preferences, false);

    if (panel.dataset.editorToolsBound !== "true") {
        panel.dataset.editorToolsBound = "true";

        panel.querySelector("[data-editor-font-size]")?.addEventListener("input", event => {
            preferences.fontSize = Number.parseInt(event.currentTarget.value, 10);
            saveAndApplyEditorPreferences(preferences);
        });

        panel.querySelector("[data-editor-font-family]")?.addEventListener("change", event => {
            preferences.fontFamily = event.currentTarget.value;
            saveAndApplyEditorPreferences(preferences);
        });

        panel.querySelectorAll("[data-direction]").forEach(button => {
            button.addEventListener("click", () => {
                preferences.direction = button.dataset.direction;
                saveAndApplyEditorPreferences(preferences);
            });
        });
    }

    queueManuscriptStats();
}

function loadEditorPreferences() {
    const defaults = { fontSize: 18, fontFamily: "system", direction: "rtl" };
    try {
        const stored = JSON.parse(localStorage.getItem(editorPreferencesKey) ?? "null");
        if (!stored) return defaults;
        const fontSize = Number.parseInt(stored.fontSize, 10);
        return {
            fontSize: Number.isFinite(fontSize) ? Math.min(28, Math.max(14, fontSize)) : defaults.fontSize,
            fontFamily: editorFontFamilies[stored.fontFamily] ? stored.fontFamily : defaults.fontFamily,
            direction: ["rtl", "ltr", "auto"].includes(stored.direction) ? stored.direction : defaults.direction
        };
    } catch {
        return defaults;
    }
}

function saveAndApplyEditorPreferences(preferences) {
    localStorage.setItem(editorPreferencesKey, JSON.stringify(preferences));
    window.sceneEditor.preferences = preferences;
    applyEditorPreferences(preferences, true);
}

function applyEditorPreferences(preferences, resizeTextareas) {
    const shell = document.querySelector(".story-shell");
    if (!shell) return;

    shell.style.setProperty("--editor-font-size", `${preferences.fontSize}px`);
    shell.style.setProperty("--editor-font-family", editorFontFamilies[preferences.fontFamily]);

    const sizeInput = document.querySelector("[data-editor-font-size]");
    const sizeOutput = document.querySelector("[data-editor-font-size-output]");
    const familyInput = document.querySelector("[data-editor-font-family]");
    if (sizeInput) sizeInput.value = preferences.fontSize;
    if (sizeOutput) sizeOutput.textContent = preferences.fontSize;
    if (familyInput) familyInput.value = preferences.fontFamily;

    document.querySelectorAll("[data-direction]").forEach(button => {
        button.setAttribute("aria-pressed", button.dataset.direction === preferences.direction ? "true" : "false");
    });

    document.querySelectorAll(".scene-content").forEach(textarea => {
        textarea.dir = preferences.direction;
        if (resizeTextareas) resizeTextarea(textarea);
    });
    queueManuscriptStats();
}

function queueManuscriptStats() {
    if (manuscriptStatsFrame !== null) return;
    manuscriptStatsFrame = window.requestAnimationFrame(() => {
        manuscriptStatsFrame = null;
        const statistics = calculateManuscriptStatistics();
        setStatistic("[data-editor-word-count]", statistics.words);
        setStatistic("[data-editor-line-count]", statistics.lines);
        setStatistic("[data-editor-page-count]", statistics.pages);
    });
}

function calculateManuscriptStatistics() {
    let words = 0;
    let lines = 0;
    document.querySelectorAll(".scene-content").forEach(textarea => {
        const text = textarea.value;
        if (!text.trim()) return;
        words += text.match(/[\p{L}\p{N}]+(?:['’״׳-][\p{L}\p{N}]+)*/gu)?.length ?? 0;

        const style = window.getComputedStyle(textarea);
        const lineHeight = Number.parseFloat(style.lineHeight);
        const padding = Number.parseFloat(style.paddingTop) + Number.parseFloat(style.paddingBottom);
        lines += lineHeight > 0 ? Math.max(1, Math.round((textarea.scrollHeight - padding) / lineHeight)) : 1;
    });

    return { words, lines, pages: words === 0 ? 0 : Math.ceil(words / 250) };
}

function setStatistic(selector, value) {
    const element = document.querySelector(selector);
    if (element) element.textContent = new Intl.NumberFormat("he-IL").format(value);
}

function handleSceneKeyDown(event) {
    if (event.key !== "Tab") return;
    event.preventDefault();
    const textarea = event.currentTarget;
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    textarea.setRangeText("\t", start, end, "end");
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
}

function queueSave(textarea, delay) {
    const sceneId = textarea.dataset.sceneTextarea;
    const existing = window.sceneEditor.saveTimers.get(sceneId);
    if (existing) window.clearTimeout(existing);
    const timer = window.setTimeout(() => flushTextarea(textarea), delay);
    window.sceneEditor.saveTimers.set(sceneId, timer);
}

async function flushTextarea(textarea) {
    const sceneId = textarea.dataset.sceneTextarea;
    if (!sceneId || !window.sceneEditor.dotNet) return;

    const timer = window.sceneEditor.saveTimers.get(sceneId);
    if (timer) {
        window.clearTimeout(timer);
        window.sceneEditor.saveTimers.delete(sceneId);
    }

    if (textarea.value === textarea.dataset.lastSaved) {
        if (localStorage.getItem(draftKey(sceneId)) === textarea.value) {
            localStorage.removeItem(draftKey(sceneId));
        }
        setSaveIndicator("saved");
        return;
    }
    const value = textarea.value;
    localStorage.setItem(draftKey(sceneId), value);
    setSaveIndicator("saving");
    try {
        await window.sceneEditor.dotNet.invokeMethodAsync("SaveSceneContent", sceneId, value);
        textarea.dataset.lastSaved = value;
        if (localStorage.getItem(draftKey(sceneId)) === value) {
            localStorage.removeItem(draftKey(sceneId));
        }
        setSaveIndicator("saved");
    } catch {
        setSaveIndicator("error");
    }
}

function updateSceneNavLabel(textarea) {
    const sceneId = textarea.dataset.sceneTextarea;
    const firstLine = textarea.value.split(/\r?\n/).map(line => line.trim()).find(Boolean) || "סצנה ריקה";
    const label = document.querySelector(`[data-scene-nav="${sceneId}"] .scene-nav-label`);
    if (label) label.textContent = firstLine.length <= 24 ? firstLine : `${firstLine.slice(0, 24)}...`;
}

function setSaveIndicator(state) {
    const indicator = document.querySelector("[data-save-indicator]");
    if (!indicator) return;
    indicator.dataset.state = state;
    indicator.textContent = state === "saving" ? "שומר..." : state === "error" ? "לא נשמר" : "נשמר";
}

function resizeTextarea(textarea) {
    textarea.style.height = "auto";
    textarea.style.height = `${Math.max(textarea.scrollHeight, 54)}px`;
    const settledHeight = Math.max(textarea.scrollHeight, 54);
    if (Math.abs(textarea.clientHeight - settledHeight) > 1) {
        textarea.style.height = `${settledHeight}px`;
    }
    sizedTextareaValues.set(textarea, textarea.value);
    sizedTextareaWidths.set(textarea, textarea.getBoundingClientRect().width);
}

function observeTextareaResize(textarea) {
    window.sceneEditor.textareaResizeObserver ??= new ResizeObserver(entries => {
        for (const entry of entries) {
            const previousWidth = sizedTextareaWidths.get(entry.target);
            const currentWidth = entry.target.getBoundingClientRect().width;
            if (previousWidth === undefined || Math.abs(currentWidth - previousWidth) > 0.5) {
                resizeTextarea(entry.target);
            }
        }
    });
    window.sceneEditor.textareaResizeObserver.observe(textarea);
}

function enableSceneMenuDrag(dotNetReference) {
    window.sceneEditor.dotNet = dotNetReference;
    document.querySelectorAll("[data-scene-nav]").forEach(item => {
        if (item.dataset.sceneDragBound === "true") return;
        item.dataset.sceneDragBound = "true";
        item.addEventListener("pointerdown", handleScenePointerDown);
        item.addEventListener("click", suppressClickAfterDrag, true);
    });
}

function handleScenePointerDown(event) {
    if (event.button !== 0) return;
    const item = event.currentTarget;
    const sceneId = item.dataset.sceneNav;
    if (!sceneId) return;

    scenePointerDrag = {
        sceneId,
        source: item,
        startX: event.clientX,
        startY: event.clientY,
        pointerId: event.pointerId,
        isDragging: false,
        targetSceneId: undefined
    };
    item.setPointerCapture?.(event.pointerId);
    window.addEventListener("pointermove", handleScenePointerMove);
    window.addEventListener("pointerup", handleScenePointerUp, { once: true });
    window.addEventListener("pointercancel", cancelScenePointerDrag, { once: true });
}

function handleScenePointerMove(event) {
    if (!scenePointerDrag) return;
    const distance = Math.hypot(event.clientX - scenePointerDrag.startX, event.clientY - scenePointerDrag.startY);
    if (!scenePointerDrag.isDragging && distance < 6) return;
    event.preventDefault();

    if (!scenePointerDrag.isDragging) {
        scenePointerDrag.isDragging = true;
        scenePointerDrag.source.classList.add("is-dragging");
        document.querySelector(".scene-outline")?.classList.add("is-reordering");
    }
    updateDropTargetFromPoint(event.clientY);
}

async function handleScenePointerUp(event) {
    if (!scenePointerDrag) return;
    if (scenePointerDrag.isDragging) {
        event.preventDefault();
        suppressNextSceneClick = true;
        const draggedId = scenePointerDrag.sceneId;
        const targetId = scenePointerDrag.targetSceneId;
        clearScenePointerDrag();
        if (targetId !== undefined && draggedId !== targetId) {
            await window.sceneEditor.flushAll();
            await window.sceneEditor.dotNet?.invokeMethodAsync("ReorderSceneFromMenu", draggedId, targetId);
        }
        window.setTimeout(() => suppressNextSceneClick = false, 0);
        return;
    }
    clearScenePointerDrag();
}

function cancelScenePointerDrag() {
    clearScenePointerDrag();
}

function updateDropTargetFromPoint(clientY) {
    document.querySelectorAll(".scene-nav-item, .scene-nav-drop-end").forEach(item => item.classList.remove("is-drop-before", "is-drop-end"));
    const navList = document.querySelector(".scene-outline");
    if (!navList || !scenePointerDrag) return;

    const items = Array.from(navList.querySelectorAll("[data-scene-nav]"))
        .filter(item => item.dataset.sceneNav !== scenePointerDrag.sceneId);
    const insertionTarget = items.find(item => {
        const rect = item.getBoundingClientRect();
        return clientY < rect.top + rect.height / 2;
    });

    if (insertionTarget) {
        insertionTarget.classList.add("is-drop-before");
        scenePointerDrag.targetSceneId = insertionTarget.dataset.sceneNav;
        return;
    }

    navList.querySelector("[data-scene-drop-end]")?.classList.add("is-drop-end");
    scenePointerDrag.targetSceneId = null;
}

function clearScenePointerDrag() {
    if (scenePointerDrag?.source) {
        scenePointerDrag.source.releasePointerCapture?.(scenePointerDrag.pointerId);
        scenePointerDrag.source.classList.remove("is-dragging");
    }
    window.removeEventListener("pointermove", handleScenePointerMove);
    window.removeEventListener("pointercancel", cancelScenePointerDrag);
    document.querySelector(".scene-outline")?.classList.remove("is-reordering");
    document.querySelectorAll(".scene-nav-item, .scene-nav-drop-end").forEach(item => item.classList.remove("is-drop-before", "is-drop-end"));
    scenePointerDrag = null;
}

function suppressClickAfterDrag(event) {
    if (!suppressNextSceneClick) return;
    event.preventDefault();
    event.stopPropagation();
}

function updateActiveSceneNav() {
    const scenes = Array.from(document.querySelectorAll(".scene-frame[id^='scene-']"));
    if (scenes.length === 0) return;
    updateActiveChapter(scenes);

    const readingLine = Math.max(100, window.innerHeight * 0.26);
    let activeScene = scenes[0];
    let smallestDistance = Number.POSITIVE_INFINITY;
    for (const scene of scenes) {
        const rect = scene.getBoundingClientRect();
        if (rect.top <= readingLine && rect.bottom >= readingLine) {
            activeScene = scene;
            break;
        }
        const distance = Math.min(Math.abs(rect.top - readingLine), Math.abs(rect.bottom - readingLine));
        if (distance < smallestDistance) {
            smallestDistance = distance;
            activeScene = scene;
        }
    }

    const activeId = activeScene.id.replace("scene-", "");
    markActiveScene(activeId, false);
}

function scheduleViewportUpdate() {
    if (viewportUpdateFrame !== null) return;
    viewportUpdateFrame = window.requestAnimationFrame(() => {
        viewportUpdateFrame = null;
        updateActiveSceneNav();
    });
}

function bindScrollTracking() {
    if (window.sceneEditor.scrollTrackingStarted) return;
    window.sceneEditor.scrollTrackingStarted = true;
    window.addEventListener("scroll", scheduleViewportUpdate, { passive: true });
    window.addEventListener("resize", scheduleViewportUpdate, { passive: true });
}

function markActiveScene(sceneId, notifyDotNet) {
    document.querySelectorAll("[data-scene-nav], [data-index-scene]").forEach(item => {
        const itemSceneId = item.dataset.sceneNav || item.dataset.indexScene;
        item.classList.toggle("is-active", itemSceneId === sceneId);
    });
    window.sceneEditor.lastActiveScene = sceneId;
    if (notifyDotNet && sceneId && sceneId !== window.sceneEditor.lastNotifiedScene) {
        window.sceneEditor.lastNotifiedScene = sceneId;
        window.sceneEditor.dotNet?.invokeMethodAsync("ActiveSceneChanged", sceneId).catch(() => {});
    }
}

function updateActiveChapter(scenes) {
    const sticky = document.querySelector(".current-chapter-divider");
    if (!sticky || scenes.length === 0) return;

    const threshold = sticky.getBoundingClientRect().bottom;
    let chapterFrame = scenes[0];
    let chapterId = chapterFrame.dataset.chapterFirst;

    for (const frame of scenes) {
        const frameChapterId = frame.dataset.chapterFirst;
        if (!frameChapterId || frameChapterId === chapterId) continue;

        const divider = frame.closest("[data-scene-stream-item]")?.querySelector(".stream-chapter-divider");
        const boundaryTop = divider?.getBoundingClientRect().top ?? frame.getBoundingClientRect().top;
        if (boundaryTop > threshold) break;

        chapterFrame = frame;
        chapterId = frameChapterId;
    }

    if (!chapterId) return;
    const chapterName = chapterFrame.dataset.chapterName || "";
    const chapterIndex = Number.parseInt(chapterFrame.dataset.chapterIndex || "0", 10);
    const stickyLabel = sticky.querySelector(".chapter-marker-label");
    if (stickyLabel) stickyLabel.textContent = chapterName;
    const removeButton = sticky.querySelector(".chapter-divider-remove");
    if (removeButton) removeButton.disabled = chapterIndex <= 0;
    document.querySelectorAll("[data-chapter-nav-first]").forEach(item => {
        item.classList.toggle("is-current", item.dataset.chapterNavFirst === chapterId);
    });

    if (chapterId && chapterId !== window.sceneEditor.lastActiveChapter) {
        window.sceneEditor.lastActiveChapter = chapterId;
        window.sceneEditor.dotNet?.invokeMethodAsync("ActiveChapterChanged", chapterId).catch(() => {});
    }
}
