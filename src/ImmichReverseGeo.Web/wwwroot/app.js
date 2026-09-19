window.downloadFile = (filename, mimeType, base64Content) => {
    const link = document.createElement('a');
    link.href = `data:${mimeType};base64,${base64Content}`;
    link.download = filename;
    link.click();
};

window.appearanceTheme = (() => {
    let mediaQuery = null;
    let changeHandler = null;

    return {
        setDataTheme(theme) {
            document.documentElement.setAttribute('data-theme', theme);
        },
        getPrefersColorScheme() {
            if (!window.matchMedia) {
                return null;
            }

            return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
        },
        subscribePrefersColorScheme(dotNetRef) {
            if (!window.matchMedia) {
                return;
            }

            mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
            changeHandler = (event) => {
                dotNetRef.invokeMethodAsync(
                    'OnBrowserSchemeChanged',
                    event.matches ? 'dark' : 'light');
            };
            mediaQuery.addEventListener('change', changeHandler);
        },
        unsubscribePrefersColorScheme() {
            if (mediaQuery && changeHandler) {
                mediaQuery.removeEventListener('change', changeHandler);
            }

            mediaQuery = null;
            changeHandler = null;
        }
    };
})();
