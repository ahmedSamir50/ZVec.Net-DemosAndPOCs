window.productSearchLens = {
  registerDropZone: function (element, dotNetRef) {
    if (!element) return;

    const prevent = (e) => {
      e.preventDefault();
      e.stopPropagation();
    };

    element.addEventListener('dragenter', prevent);
    element.addEventListener('dragover', prevent);
    element.addEventListener('dragleave', prevent);
    element.addEventListener('drop', async (e) => {
      prevent(e);
      const file = e.dataTransfer?.files?.[0];
      if (!file || !file.type.startsWith('image/')) return;
      const payload = await readFilePayload(file);
      await dotNetRef.invokeMethodAsync('OnLensFileReceived', payload.base64, payload.contentType, payload.blobUrl);
    });

    element.addEventListener('paste', async (e) => {
      const items = e.clipboardData?.items;
      if (!items) return;
      for (const item of items) {
        if (item.type.startsWith('image/')) {
          e.preventDefault();
          const file = item.getAsFile();
          if (!file) return;
          const payload = await readFilePayload(file);
          await dotNetRef.invokeMethodAsync('OnLensFileReceived', payload.base64, payload.contentType, payload.blobUrl);
          return;
        }
      }

      const text = e.clipboardData?.getData('text/plain')?.trim();
      if (text && /^https?:\/\//i.test(text)) {
        e.preventDefault();
        await dotNetRef.invokeMethodAsync('OnLensUrlReceived', text);
      }
    });
  },

  revokeBlobUrl: function (url) {
    if (url && url.startsWith('blob:')) {
      try { URL.revokeObjectURL(url); } catch { /* ignore */ }
    }
  }
};

function readFilePayload(file) {
  return new Promise((resolve, reject) => {
    const blobUrl = URL.createObjectURL(file);
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result;
      if (typeof result !== 'string') {
        URL.revokeObjectURL(blobUrl);
        reject(new Error('Unexpected file reader result'));
        return;
      }
      const comma = result.indexOf(',');
      resolve({
        base64: comma >= 0 ? result.slice(comma + 1) : result,
        contentType: file.type,
        blobUrl
      });
    };
    reader.onerror = () => {
      URL.revokeObjectURL(blobUrl);
      reject(reader.error ?? new Error('Failed to read file'));
    };
    reader.readAsDataURL(file);
  });
}
