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
      const base64 = await readFileAsBase64(file);
      await dotNetRef.invokeMethodAsync('OnLensFileReceived', base64, file.type);
    });

    element.addEventListener('paste', async (e) => {
      const items = e.clipboardData?.items;
      if (!items) return;
      for (const item of items) {
        if (item.type.startsWith('image/')) {
          e.preventDefault();
          const file = item.getAsFile();
          if (!file) return;
          const base64 = await readFileAsBase64(file);
          await dotNetRef.invokeMethodAsync('OnLensFileReceived', base64, file.type);
          break;
        }
      }
    });
  }
};

function readFileAsBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result;
      if (typeof result !== 'string') {
        reject(new Error('Unexpected file reader result'));
        return;
      }
      const comma = result.indexOf(',');
      resolve(comma >= 0 ? result.slice(comma + 1) : result);
    };
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read file'));
    reader.readAsDataURL(file);
  });
}
