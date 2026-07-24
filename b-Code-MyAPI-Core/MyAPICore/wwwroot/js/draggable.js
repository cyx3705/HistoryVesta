// draggable.js - 最终修复版
console.log("=== draggable.js 成功加载 ===");

window.makeDraggable = (elementId, dotNetHelper) => {
    console.log(`makeDraggable called for element: ${elementId}`);

    const el = document.getElementById(elementId);
    if (!el) {
        console.error(`Element not found: ${elementId}`);
        return;
    }

    console.log(`Element found! Binding drag events to ${elementId}`);

    let pos1 = 0, pos2 = 0, pos3 = 0, pos4 = 0;
    let helper = dotNetHelper;   // 保存引用，避免闭包丢失

    el.onmousedown = dragMouseDown;

    function dragMouseDown(e) {
        e.preventDefault();
        pos3 = e.clientX;
        pos4 = e.clientY;

        document.onmouseup = closeDragElement;
        document.onmousemove = elementDrag;
    }

    function elementDrag(e) {
        e.preventDefault();

        pos1 = pos3 - e.clientX;
        pos2 = pos4 - e.clientY;
        pos3 = e.clientX;
        pos4 = e.clientY;

        const newLeft = el.offsetLeft - pos1;
        const newTop = el.offsetTop - pos2;

        el.style.left = newLeft + "px";
        el.style.top = newTop + "px";

        // 使用保存的 helper
        if (helper && helper.invokeMethodAsync) {
            helper.invokeMethodAsync("UpdatePosition", newLeft, newTop);
        } else {
            console.warn("dotNetHelper is undefined or invalid");
        }
    }

    function closeDragElement() {
        document.onmouseup = null;
        document.onmousemove = null;
    }
};