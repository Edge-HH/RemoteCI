// 自动化规则编辑页：只显示当前触发方式与动作类型对应的字段；隐藏的字段仍随表单提交，由服务端按类型取用。
(() => {
    const form = document.querySelector('[data-automation-form]');
    if (!form) return;

    const trigger = form.querySelector('[data-trigger-kind]');
    function syncTrigger() {
        form.querySelectorAll('[data-trigger]').forEach(section => {
            section.hidden = section.dataset.trigger !== trigger.value;
        });
    }
    trigger.addEventListener('change', syncTrigger);
    syncTrigger();

    form.querySelectorAll('[data-action]').forEach(block => {
        const kind = block.querySelector('[data-action-kind]');
        const sync = () => block.querySelectorAll('[data-action-fields]').forEach(fields => {
            fields.hidden = fields.dataset.actionFields !== kind.value;
        });
        kind.addEventListener('change', sync);
        sync();
    });
})();
