/** An image the League client serves (skin tiles, rune icons), loaded through the host so a phone can show it. */
export const asset = (path: string): string => (path ? '/api/lcu-asset?path=' + encodeURIComponent(path) : '')
