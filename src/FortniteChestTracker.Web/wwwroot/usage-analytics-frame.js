// This classic script runs in an opaque-origin sandbox. Google never receives
// access to the report DOM, localStorage, IndexedDB, selected files, or parent globals.
(() => {
  const ORIGIN = 'https://loukious.github.io';
  const PAGE = ORIGIN + '/FortniteChestTracker/';
  const EVENTS = new Set(['page_view', 'scan_started', 'csv_exported', 'history_exported', 'history_import_opened', 'folder_picker_opened', 'files_picker_opened']);
  let channel, measurementId, configured = false, disabled = false;
  // A native cookie read throws in an opaque frame, even with storage consent
  // denied. Supply an empty, inert cookie interface for gtag's bootstrap. This
  // never accesses real cookies and does not change the browser security origin.
  Object.defineProperty(document, 'cookie', {
    configurable: false,
    get: () => '',
    set: () => {}
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
      // Loading and sending are opt-in in the parent. The Google tag itself is
      // never granted cookie storage; only a separate random usage ID is passed.
      gtag('consent', 'default', { analytics_storage: 'denied', ad_storage: 'denied', ad_user_data: 'denied', ad_personalization: 'denied' });
      gtag('set', { client_id: data.clientId, send_page_view: false, page_location: PAGE,
        page_title: 'Fortnite Chest Tracker', page_referrer: '', ignore_referrer: true,
        allow_google_signals: false, allow_ad_personalization_signals: false,
        cookie_update: false });
      gtag('js', new Date());
      const script = document.createElement('script');
      script.async = true; script.referrerPolicy = 'no-referrer';
      script.src = 'https://www.googletagmanager.com/gtag/js?id=' + measurementId;
      script.onload = () => {
        if (disabled) return;
        gtag('config', measurementId, { client_id: data.clientId, send_page_view: false, page_location: PAGE,
          page_title: 'Fortnite Chest Tracker', page_referrer: '', ignore_referrer: true,
          allow_google_signals: false, allow_ad_personalization_signals: false, cookie_update: false });
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
