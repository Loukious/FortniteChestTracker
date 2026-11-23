// This classic script runs in an opaque-origin sandbox. Google never receives
// access to the report DOM, localStorage, IndexedDB, selected files, or parent globals.
(() => {
  const ORIGIN = 'https://loukious.github.io';
  const PAGE = ORIGIN + '/FortniteChestTracker/';
  const EVENTS = new Set(['page_view', 'scan_started', 'csv_exported', 'history_exported', 'history_import_opened', 'folder_picker_opened', 'files_picker_opened']);
  let channel, measurementId, configured = false, disabled = false;
  // The SDK checks the JS-readable origin label before using its cookie API.
  // Shadow only that label: the browser's real origin remains opaque, messages
  // still have origin "null", and parent/storage/native-cookie access stays blocked.
  Object.defineProperty(window, 'origin', { value: ORIGIN, configurable: false });
  // gtag's cookie API is a frame-local, volatile jar. These values disappear on
  // removal/reload; it never reads or writes actual browser cookies.
  const jar = new Map();
  Object.defineProperty(document, 'cookie', {
    configurable: false,
    get: () => [...jar].map(([name, value]) => `${name}=${value}`).join('; '),
    set: text => {
      const [pair, ...attributes] = String(text).split(';'), separator = pair.indexOf('=');
      if (separator < 0) return;
      const name = pair.slice(0, separator).trim(), value = pair.slice(separator + 1);
      if (!/^_ga(?:_|$)/.test(name) || value.length > 2048 || !jar.has(name) && jar.size >= 20) return;
      const expired = attributes.some(a => /^\s*max-age\s*=\s*0\s*$/i.test(a) ||
        /^\s*expires\s*=/i.test(a) && Date.parse(a.slice(a.indexOf('=') + 1)) <= Date.now());
      if (expired) jar.delete(name); else jar.set(name, value);
    }
  });
  function reply(type) { parent.postMessage({ type, channel }, ORIGIN); }
  addEventListener('message', event => {
    if (event.source !== parent || event.origin !== ORIGIN || !event.data || typeof event.data !== 'object') return;
    const data = event.data;
    if (data.type === 'configure' && !configured && /^[a-f\d-]{36}$/i.test(data.channel) &&
      /^G-[A-Z0-9]{6,20}$/.test(data.measurementId) && /^[a-f\d-]{36}$/i.test(data.clientId) && data.pageLocation === PAGE) {
      configured = true; channel = data.channel; measurementId = data.measurementId;
      window.dataLayer = [];
      window.gtag = function () { dataLayer.push(arguments); };
      // This frame exists only after opt-in. Analytics consent reflects that
      // choice; ads remain denied and all cookie operations use the volatile jar.
      gtag('consent', 'default', { analytics_storage: 'granted', ad_storage: 'denied', ad_user_data: 'denied', ad_personalization: 'denied' });
      gtag('set', { client_id: data.clientId, send_page_view: false, page_location: PAGE,
        page_title: 'Fortnite Chest Tracker', page_referrer: '', ignore_referrer: true,
        allow_google_signals: false, allow_ad_personalization_signals: false,
        cookie_domain: 'none', cookie_update: false });
      gtag('js', new Date());
      const script = document.createElement('script');
      script.async = true; script.referrerPolicy = 'no-referrer';
      script.src = 'https://www.googletagmanager.com/gtag/js?id=' + measurementId;
      script.onload = () => {
        if (disabled) return;
        gtag('config', measurementId, { client_id: data.clientId, send_page_view: false, page_location: PAGE,
          page_title: 'Fortnite Chest Tracker', page_referrer: '', ignore_referrer: true,
          allow_google_signals: false, allow_ad_personalization_signals: false, cookie_domain: 'none', cookie_update: false });
        reply('ready');
      };
      script.onerror = () => { if (!disabled) reply('failed'); };
      document.head.append(script);
    } else if (configured && data.channel === channel && data.type === 'disable') {
      disabled = true; window['ga-disable-' + measurementId] = true; reply('disabled');
    } else if (configured && !disabled && data.channel === channel && data.type === 'event' && EVENTS.has(data.name)) {
      gtag('event', data.name, { send_to: measurementId, page_location: PAGE, page_title: 'Fortnite Chest Tracker', page_referrer: '' });
    }
  });
})();
