/* 本地输入适配：保留 Emby DOM、路由和播放器，只增加焦点样式与按需键盘。 */
(() => {
    'use strict';
    if (location.origin !== 'https://emby_strm-home.172906573.xyz:28443') return;
    // 保留浏览器原生输入给本地桥；Emby 自己的轮询不可再次消费同一按键。
    const readGamepads = navigator.getGamepads ? navigator.getGamepads.bind(navigator) : () => [];
    try { Object.defineProperty(navigator, 'getGamepads', { value: () => [], configurable: true }); } catch (_) { /* 不支持时仍由宿主负责输入。 */ }
    let keyboard = null, editor = null, upper = false;
    let nativeUntil = 0, lastAction = '', lastSource = '', lastAt = -Infinity;
    let held = '', repeatAt = 0, deviceIndex = -1;
    const selector = 'a[href],button,input:not([type=hidden]),select,textarea,[tabindex], [role=button]';

    /** 返回可见且可操作的节点，弹窗打开时只允许在弹窗内导航。 */
    function candidates() {
        const dialogs = [...document.querySelectorAll('[role=dialog],dialog[open],.dialog')].filter(visible);
        const scope = keyboard || dialogs[dialogs.length - 1] || document;
        return [...scope.querySelectorAll(selector)].filter(e => visible(e) && !e.disabled && e.getAttribute('aria-disabled') !== 'true' && e.tabIndex >= 0);
    }
    /** 剔除隐藏节点和被父级隐藏的元素；允许滚动区外节点参与导航。 */
    function visible(e) {
        const r = e.getBoundingClientRect(), s = getComputedStyle(e);
        return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && !e.closest('[hidden],[inert],[aria-hidden=true]');
    }
    /** 将焦点滚动至可视区；不改写 Emby 自己的卡片大小、颜色与布局。 */
    function focus(e) {
        if (!e) return;
        document.querySelectorAll('[data-shell-focus]').forEach(node => node.removeAttribute('data-shell-focus'));
        e.setAttribute('data-shell-focus','');
        e.focus({ preventScroll: true }); e.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
    }
    /** 使用前向距离与侧向距离评分，支持横向海报轨道和多行媒体墙。 */
    function move(direction) {
        const items = candidates(), current = document.activeElement;
        if (!items.includes(current)) { focus(items.find(e => {const r=e.getBoundingClientRect(); return r.top>=0 && r.bottom<=innerHeight;}) || items[0]); return; }
        const r = current.getBoundingClientRect(), horizontal = direction === 'left' || direction === 'right';
        const sign = direction === 'left' || direction === 'up' ? -1 : 1;
        const x = r.left + r.width / 2, y = r.top + r.height / 2;
        let winner = null, best = Infinity;
        for (const e of items) {
            if (e === current) continue;
            const b = e.getBoundingClientRect(), dx = b.left + b.width / 2 - x, dy = b.top + b.height / 2 - y;
            const forward = (horizontal ? dx : dy) * sign, side = Math.abs(horizontal ? dy : dx);
            if (forward < 3) continue;
            const score = forward + side * 3;
            if (score < best) { best = score; winner = e; }
        }
        if (winner) focus(winner);
        else focus(current);
    }
    /** 通过原生值 setter 通知网页框架，密码只留在输入框而不进入宿主消息。 */
    function edit(value) {
        if (!editor.isConnected) editor = replacement(editor);
        if (!editor) { closeKeyboard(); return; }
        const proto = editor.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        Object.getOwnPropertyDescriptor(proto, 'value').set.call(editor, value);
        editor.dispatchEvent(new Event('input', { bubbles: true }));
        editor.dispatchEvent(new Event('change', { bubbles: true }));
    }
    /** 完成或取消键盘时恢复原输入框焦点；不自动提交表单。 */
    function closeKeyboard() {
        if (!keyboard) return;
        keyboard.remove(); keyboard = null; focus(editor && editor.isConnected ? editor : replacement(editor)); editor = null;
    }
    /** 前端重新挂载输入框时按稳定 id/name 恢复；找不到时返回第一个可操作节点。 */
    function replacement(previous) {
        if (previous && previous.id) { const found=document.getElementById(previous.id); if(found)return found; }
        if (previous && previous.name) { const found=[...document.getElementsByName(previous.name)].find(visible); if(found)return found; }
        return null;
    }
    /** 输入框确认后提供手柄键盘，实体键盘仍可直接输入中文或其他文字。 */
    function openKeyboard(input) {
        if (keyboard) return;
        editor = input; keyboard = document.createElement('div'); keyboard.id = 'shell-keyboard'; keyboard.setAttribute('role','dialog'); keyboard.setAttribute('aria-label','手柄键盘');
        const heading = document.createElement('div'); heading.textContent = '文字输入 · ○ 完成返回 · 支持实体键盘输入中文'; heading.style.gridColumn = '1 / -1'; keyboard.append(heading);
        for (const key of [...'abcdefghijklmnopqrstuvwxyz0123456789', '@', '.', '-', '_', '/', ':', '!', '?', '#', '$', '%', '&', '+', '=', '大写', '空格', '删除', '完成']) {
            const button = document.createElement('button'); button.type = 'button'; button.textContent = key;
            button.onclick = () => {
                if (key === '完成') { closeKeyboard(); return; }
                if (key === '大写') { upper = !upper; button.textContent = upper ? '小写' : '大写'; return; }
                if (key === '删除') edit(editor.value.slice(0,-1));
                else edit(editor.value + (key === '空格' ? ' ' : upper ? key.toUpperCase() : key));
            };
            keyboard.append(button);
        }
        document.body.append(keyboard); focus(keyboard.querySelector('button'));
    }
    /** 确认优先使用页面真实控件，文字框改为本地键盘，滑块仍接受左右调整。 */
    function accept() {
        let e = document.activeElement;
        if (!candidates().includes(e)) { focus(candidates()[0]); return; }
        if (!keyboard && ((e.tagName === 'INPUT' && !['checkbox','radio','range','submit','button','file'].includes(e.type)) || e.tagName === 'TEXTAREA')) openKeyboard(e);
        else e.click();
    }
    /** 返回先关闭输入层或网页弹窗，其次调用页面后退按钮，最后使用浏览器历史。 */
    function back() {
        if (keyboard) { closeKeyboard(); return; }
        const close = [...document.querySelectorAll('.dialog .btnCancel,.dialog [data-action=close],.btnBack,.headerButtonLeft')].find(visible);
        if (close) close.click();
        else if (history.length > 1) history.back();
    }
    /** 固定动作入口只供宿主调用，不接收任意脚本或读取账号信息。 */
    window.shellController = (action, source = 'host') => {
        const now = performance.now();
        // 原生设备已接管时，SDK 仍可报告按键，但不能执行第二次动作。
        if (source === 'host' && now < nativeUntil) return;
        if (action === lastAction && source !== lastSource && now - lastAt < 100) return;
        lastAction = action; lastSource = source; lastAt = now;
        if (action === 'exit') {
            if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage('shell:exit');
            return;
        }
        if (['left','right','up','down'].includes(action)) {
            const e = document.activeElement;
            if (e && e.type === 'range' && (action === 'left' || action === 'right')) {
                action === 'left' ? e.stepDown() : e.stepUp(); e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true}));
            } else move(action);
        } else if (action === 'accept') accept();
        else if (action === 'back') back();
        else if (action === 'previousPage' || action === 'nextPage') move(action === 'previousPage' ? 'up' : 'down');
        else if (action === 'trailer') {
            const video = document.querySelector('video');
            if (video) { if (video.paused) video.play().catch(() => {}); else video.pause(); }
        }
    };
    /** 使用标准 Gamepad 映射读取十字键、左摇杆和按钮，只有方向键允许按住重复。 */
    function pollGamepad() {
        if (document.visibilityState === 'hidden' || !document.hasFocus()) { held=''; nativeUntil=0; requestAnimationFrame(pollGamepad); return; }
        let pad;
        try { pad=Array.from(readGamepads()).find(p=>p && p.connected && p.mapping==='standard'); } catch (_) { /* 未授权或设备尚未公开时继续依赖 SDK。 */ }
        if (!pad) { held=''; nativeUntil=0; deviceIndex=-1; requestAnimationFrame(pollGamepad); return; }
        const now=performance.now(); nativeUntil=now+250;
        if (deviceIndex!==pad.index) { deviceIndex=pad.index; held=''; }
        const pressed=i=>!!(pad.buttons[i] && pad.buttons[i].pressed);
        const action=pressed(9)||pressed(3)?'exit':pressed(14)||(pad.axes[0]||0)<-.5?'left':pressed(15)||(pad.axes[0]||0)>.5?'right':pressed(12)||(pad.axes[1]||0)<-.5?'up':pressed(13)||(pad.axes[1]||0)>.5?'down':pressed(0)?'accept':pressed(1)?'back':pressed(2)?'trailer':pressed(4)?'previousPage':pressed(5)?'nextPage':'';
        if (action!==held) {
            held=action; repeatAt=now+350;
            if(action)window.shellController(action,'native');
        } else if (['left','right','up','down'].includes(action) && now>=repeatAt) {
            repeatAt=now+120; window.shellController(action,'native');
        }
        requestAnimationFrame(pollGamepad);
    }
    requestAnimationFrame(pollGamepad);
    /** 只注入手柄焦点框和输入层 CSS；服务自己的媒体布局保持原样。 */
    document.addEventListener('DOMContentLoaded', () => {
        const style = document.createElement('style');
        style.textContent = ':focus-visible,[data-shell-focus]{outline:3px solid #a4e8ff!important;outline-offset:3px!important}#shell-keyboard{position:fixed;z-index:2147483647;bottom:2vh;left:15vw;width:70vw;box-sizing:border-box;padding:24px;background:#162332f5;border:1px solid #89badb;border-radius:14px;display:grid;grid-template-columns:repeat(10,1fr);gap:8px;color:white;font:18px sans-serif}#shell-keyboard button{background:#ffffff16;border:1px solid #ffffff45;color:white;padding:12px 4px;font:20px sans-serif;border-radius:6px}#shell-keyboard button:focus{outline:3px solid #a4e8ff;background:#ffffff40}';
        document.head.append(style);
        document.addEventListener('keydown', e => { if (e.key === 'Escape' && keyboard) { e.preventDefault(); e.stopImmediatePropagation(); closeKeyboard(); } },true);
    },{once:true});
})();
