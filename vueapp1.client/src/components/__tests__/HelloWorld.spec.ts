import { describe, it, expect, vi, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import HelloWorld from '../HelloWorld.vue';

describe('HelloWorld', () => {
  beforeEach(() => {
    global.fetch = vi.fn(() =>
      Promise.resolve({
        ok: true,
        json: () => Promise.resolve([]),
      } as Response),
    );
  });

  it('renders properly', () => {
    const wrapper = mount(HelloWorld);
    expect(wrapper.find('h1').text()).toBe('Weather forecast');
    expect(wrapper.find('p').text()).toContain(
      'This component demonstrates fetching data from the server.',
    );
  });

  it('shows loading state initially', async () => {
    global.fetch = vi.fn(
      () => new Promise<Response>(() => {}), // Never resolves to keep loading state
    );

    const wrapper = mount(HelloWorld);
    await nextTick();

    expect(wrapper.find('.loading').exists()).toBe(true);
  });

  it('displays weather data when fetch succeeds', async () => {
    const mockData = [
      {
        date: '2024-01-01',
        temperatureC: 20,
        temperatureF: 68,
        summary: 'Warm',
      },
      {
        date: '2024-01-02',
        temperatureC: 15,
        temperatureF: 59,
        summary: 'Cool',
      },
    ];

    global.fetch = vi.fn(() =>
      Promise.resolve({
        ok: true,
        json: async () => mockData,
      } as Response),
    );

    const wrapper = mount(HelloWorld);

    // Wait for component to mount and fetch to complete
    await new Promise((resolve) => setTimeout(resolve, 100));
    await nextTick();

    const rows = wrapper.findAll('tbody tr');
    expect(rows).toHaveLength(2);

    expect(rows[0].text()).toContain('2024-01-01');
    expect(rows[0].text()).toContain('20');
    expect(rows[0].text()).toContain('68');
    expect(rows[0].text()).toContain('Warm');
  });

  it('handles fetch error gracefully', async () => {
    const consoleErrorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    global.fetch = vi.fn(() => Promise.reject(new Error('Network error')));

    const wrapper = mount(HelloWorld);

    // Wait for fetch to fail
    await new Promise((resolve) => setTimeout(resolve, 100));
    await nextTick();

    expect(consoleErrorSpy).toHaveBeenCalledWith('Error fetching weather data:', expect.any(Error));
    expect(wrapper.find('.loading').exists()).toBe(false);

    consoleErrorSpy.mockRestore();
  });
});
