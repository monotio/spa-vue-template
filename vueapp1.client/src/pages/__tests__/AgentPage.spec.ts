import { describe, it, expect, vi, beforeEach } from 'vitest';
import { computed, ref } from 'vue';
import { mount } from '@vue/test-utils';
import { createMockedComposable } from '@/test/mockComposable';
import type {
  AgentChatMessage,
  AgentPendingApproval,
  AgentStreamStatus,
  AgentUsageTotals,
} from '@/composables/useAgentStream';
import { AGENT_ATTACHMENT_LIMITS, type AgentFinishReason } from '@/contracts/agent';

const agentMock = createMockedComposable(() => {
  const status = ref<AgentStreamStatus>('idle');
  return {
    status,
    messages: ref<AgentChatMessage[]>([]),
    pendingApproval: ref<AgentPendingApproval>(),
    errorMessage: ref<string>(),
    agentDisabled: ref(false),
    budgetResetAtUtc: ref<string>(),
    lastFinishReason: ref<AgentFinishReason>(),
    usage: ref<AgentUsageTotals>({
      inputTokens: 0,
      cachedInputTokens: 0,
      outputTokens: 0,
      reasoningTokens: 0,
      estimatedUsd: 0,
    }),
    isRestoring: ref(false),
    isStreaming: computed(() => status.value === 'streaming'),
    // Typed so tests can inspect mock.calls — the page re-wraps extension-only
    // files, so toHaveBeenCalledWith(file) identity checks don't fit that case.
    sendMessage: vi.fn<(text: string, files?: readonly File[]) => Promise<void>>(() =>
      Promise.resolve(),
    ),
    approve: vi.fn(() => Promise.resolve()),
    reject: vi.fn(() => Promise.resolve()),
    abort: vi.fn(),
    restore: vi.fn(() => Promise.resolve()),
  };
});

vi.mock('@/composables/useAgentStream', () => ({ useAgentStream: agentMock.mock }));

// Dynamic import AFTER the mock wiring: the mocked module factory runs when
// the page module loads, which must happen after `agentMock` exists.
const AgentPage = (await import('../AgentPage.vue')).default;

beforeEach(agentMock.reset);

describe('AgentPage', () => {
  it('restores the conversation on mount (replay through the shared reducer)', () => {
    const state = agentMock.mock();
    mount(AgentPage);

    expect(state.restore).toHaveBeenCalledTimes(1);
  });

  it('renders the friendly disabled-state callout when the module is off', () => {
    const state = agentMock.mock();
    state.agentDisabled.value = true;

    const wrapper = mount(AgentPage);

    const callout = wrapper.get('[data-testid="agent-disabled"]');
    expect(callout.text()).toContain('Agent:Enabled');
    expect(callout.text()).toContain('docs/AGENT.md');
    // The composer is hidden — there is nothing to send to.
    expect(wrapper.find('form').exists()).toBe(false);
  });

  it('renders the transcript: user text, agent text, and tool cards', () => {
    const state = agentMock.mock();
    state.messages.value = [
      { role: 'user', items: [{ kind: 'text', text: 'Weather please', streaming: false }] },
      {
        role: 'assistant',
        items: [
          { kind: 'reasoning', text: 'check the registry', streaming: false },
          {
            kind: 'tool',
            toolCallId: 'call-1',
            toolName: 'get_weather_forecast',
            argumentsJson: '{"days":3}',
            status: 'ok',
            resultJson: '{"result":[]}',
          },
          { kind: 'text', text: 'Sunny.', streaming: false },
        ],
      },
    ];

    const wrapper = mount(AgentPage);

    expect(wrapper.text()).toContain('Weather please');
    expect(wrapper.text()).toContain('Sunny.');
    expect(wrapper.text()).toContain('check the registry');
    const card = wrapper.getComponent({ name: 'ToolCallCard' });
    expect(card.text()).toContain('get_weather_forecast');
    expect(card.text()).toContain('Completed');
  });

  it('sends the draft through the composable and clears the input', async () => {
    const state = agentMock.mock();
    const wrapper = mount(AgentPage);

    await wrapper.get('#agent-message').setValue('  hello agent  ');
    await wrapper.get('form').trigger('submit');

    expect(state.sendMessage).toHaveBeenCalledWith('  hello agent  ', []);
    expect((wrapper.get('#agent-message').element as HTMLInputElement).value).toBe('');
  });

  it('attaches valid files as removable chips and sends them with the message', async () => {
    const state = agentMock.mock();
    const wrapper = mount(AgentPage);
    const file = new File(['x'], 'pic.png', { type: 'image/png' });

    const input = wrapper.get('input[type="file"]');
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true });
    await input.trigger('change');

    expect(wrapper.get('[data-testid="pending-attachments"]').text()).toContain('pic.png');

    await wrapper.get('#agent-message').setValue('what is this?');
    await wrapper.get('form').trigger('submit');

    // Upload-then-reference is the composable's job — the page hands over
    // the raw Files and clears its composer state.
    expect(state.sendMessage).toHaveBeenCalledWith('what is this?', [file]);
    expect(wrapper.find('[data-testid="pending-attachments"]').exists()).toBe(false);
  });

  it('rejects oversized and unsupported files with a visible message (mirrors server limits)', async () => {
    agentMock.mock();
    const wrapper = mount(AgentPage);
    const tooBig = new File(['x'], 'big.png', { type: 'image/png' });
    Object.defineProperty(tooBig, 'size', { value: AGENT_ATTACHMENT_LIMITS.maxBytes + 1 });
    const wrongType = new File(['x'], 'tool.zip', { type: 'application/zip' });

    const input = wrapper.get('input[type="file"]');
    Object.defineProperty(input.element, 'files', {
      value: [tooBig, wrongType],
      configurable: true,
    });
    await input.trigger('change');

    const error = wrapper.get('[data-testid="attachment-error"]');
    expect(error.text()).toContain('exceeds');
    expect(error.text()).toContain('unsupported');
    expect(wrapper.find('[data-testid="pending-attachments"]').exists()).toBe(false);
  });

  it('falls back to the file extension when the browser leaves file.type empty', async () => {
    const state = agentMock.mock();
    const wrapper = mount(AgentPage);
    // Browsers report an empty MIME type for extensions outside their
    // registry (.md is the common casualty): the composer must map the
    // extension onto the mirrored allowlist instead of rejecting the file.
    const noType = new File(['# notes'], 'notes.md', { type: '' });

    const input = wrapper.get('input[type="file"]');
    Object.defineProperty(input.element, 'files', { value: [noType], configurable: true });
    await input.trigger('change');

    expect(wrapper.find('[data-testid="attachment-error"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="pending-attachments"]').text()).toContain('notes.md');

    await wrapper.get('#agent-message').setValue('summarize this');
    await wrapper.get('form').trigger('submit');

    // The multipart part's Content-Type comes from File.type — an empty
    // type would reach the server as application/octet-stream and 415, so
    // the page re-wraps the file with the inferred media type.
    expect(state.sendMessage).toHaveBeenCalledTimes(1);
    const sentFiles = state.sendMessage.mock.calls[0]?.[1] ?? [];
    expect(sentFiles).toHaveLength(1);
    expect(sentFiles[0]?.name).toBe('notes.md');
    expect(sentFiles[0]?.type).toBe('text/markdown');
  });

  it('still rejects an unknown extension when file.type is empty', async () => {
    agentMock.mock();
    const wrapper = mount(AgentPage);
    const unknown = new File(['x'], 'firmware.xyz', { type: '' });

    const input = wrapper.get('input[type="file"]');
    Object.defineProperty(input.element, 'files', { value: [unknown], configurable: true });
    await input.trigger('change');

    expect(wrapper.get('[data-testid="attachment-error"]').text()).toContain('unsupported');
    expect(wrapper.find('[data-testid="pending-attachments"]').exists()).toBe(false);
  });

  it('removes a pending chip before sending', async () => {
    const state = agentMock.mock();
    const wrapper = mount(AgentPage);
    const file = new File(['x'], 'pic.png', { type: 'image/png' });
    const input = wrapper.get('input[type="file"]');
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true });
    await input.trigger('change');

    await wrapper.get('button.chip-remove').trigger('click');
    expect(wrapper.find('[data-testid="pending-attachments"]').exists()).toBe(false);

    await wrapper.get('#agent-message').setValue('no files after all');
    await wrapper.get('form').trigger('submit');
    expect(state.sendMessage).toHaveBeenCalledWith('no files after all', []);
  });

  it('renders attachment chips in the transcript for file items', () => {
    const state = agentMock.mock();
    state.messages.value = [
      {
        role: 'user',
        items: [
          { kind: 'text', text: 'summarize this', streaming: false },
          { kind: 'file', fileName: 'notes.txt', mediaType: 'text/plain' },
        ],
      },
    ];

    const wrapper = mount(AgentPage);

    const chip = wrapper.getComponent({ name: 'AttachmentChip' });
    expect(chip.text()).toContain('notes.txt');
    expect(chip.text()).toContain('text/plain');
    // Transcript chips are display-only; removal exists only in the composer.
    expect(chip.find('button.chip-remove').exists()).toBe(false);
  });

  it('wires the approval card round-trip to approve()', async () => {
    const state = agentMock.mock();
    state.status.value = 'awaiting-approval';
    state.pendingApproval.value = {
      toolCallId: 'call-9',
      toolName: 'delete_item',
      argumentsJson: '{"id":7}',
    };

    const wrapper = mount(AgentPage);
    await wrapper.get('button.approve').trigger('click');

    expect(state.approve).toHaveBeenCalledWith('call-9', undefined);
  });

  it('wires rejection with the optional reason', async () => {
    const state = agentMock.mock();
    state.pendingApproval.value = {
      toolCallId: 'call-9',
      toolName: 'delete_item',
      argumentsJson: '{}',
    };

    const wrapper = mount(AgentPage);
    await wrapper.get('#approval-reason').setValue('not in production');
    await wrapper.get('button.reject').trigger('click');

    expect(state.reject).toHaveBeenCalledWith('call-9', 'not in production');
  });

  it('locks the composer while an approval is pending', async () => {
    const state = agentMock.mock();
    state.status.value = 'awaiting-approval';
    state.pendingApproval.value = {
      toolCallId: 'call-9',
      toolName: 'delete_item',
      argumentsJson: '{}',
    };

    const wrapper = mount(AgentPage);

    // Sending a new message with a frozen approval hands the provider a
    // transcript with an unanswered tool call — the composer must not offer
    // the wedging action (mirrors the sendMessage guard in the composable).
    expect((wrapper.get('#agent-message').element as HTMLInputElement).disabled).toBe(true);
    await wrapper.get('#agent-message').setValue('detour message');
    await wrapper.get('form').trigger('submit');
    expect(state.sendMessage).not.toHaveBeenCalled();
  });

  it('shows Stop while streaming and forwards it to abort()', async () => {
    const state = agentMock.mock();
    state.status.value = 'streaming';

    const wrapper = mount(AgentPage);
    await wrapper.get('button.stop').trigger('click');

    expect(state.abort).toHaveBeenCalledTimes(1);
  });

  it('renders the usage footer once tokens have been billed', () => {
    const state = agentMock.mock();
    state.usage.value = {
      inputTokens: 1200,
      cachedInputTokens: 800,
      outputTokens: 250,
      reasoningTokens: 64,
      estimatedUsd: 0.0173,
    };

    const wrapper = mount(AgentPage);

    const footer = wrapper.get('[data-testid="usage-footer"]');
    expect(footer.text()).toContain('1200 in (800 cached)');
    expect(footer.text()).toContain('250 out');
    expect(footer.text()).toContain('$0.0173');
  });

  it('surfaces errors with the budget reset hint when present', () => {
    const state = agentMock.mock();
    state.errorMessage.value = 'Daily agent budget exhausted';
    state.budgetResetAtUtc.value = '2026-06-13T00:00:00+00:00';

    const wrapper = mount(AgentPage);

    const alert = wrapper.get('[role="alert"]');
    expect(alert.text()).toContain('Daily agent budget exhausted');
    expect(alert.text()).toContain('2026-06-13T00:00:00+00:00');
  });
});
