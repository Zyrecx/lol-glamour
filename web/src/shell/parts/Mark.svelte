<script lang="ts">
  // The Blossom mark: five petals under one teal-violet-magenta gradient around a gold core (SPEC "UI (decided)"); the same drawing as
  // public/favicon.svg and the desktop's BlossomIcon, without the tile. Lowered 3.5 so the flower, not its core, sits centred.
  let { size = 24 }: { size?: number } = $props()
  const id = `bm${Math.random().toString(36).slice(2, 8)}`
</script>

<svg width={size} height={size} viewBox="0 0 100 100" aria-hidden="true">
  <defs>
    <linearGradient id={`${id}g`} gradientUnits="userSpaceOnUse" x1="22" y1="14" x2="78" y2="86">
      <stop offset="0" stop-color="#2DE2C4" /><stop offset=".5" stop-color="#8C7BF2" /><stop offset="1" stop-color="#E24FA6" />
    </linearGradient>
    <clipPath id={`${id}p`}>
      {#each [0, 72, 144, 216, 288] as r (r)}<ellipse cx="50" cy="27" rx="13" ry="22" transform={`rotate(${r} 50 50)`} />{/each}
    </clipPath>
  </defs>
  <g transform="translate(0 3.5)">
    <rect width="100" height="100" fill={`url(#${id}g)`} clip-path={`url(#${id}p)`} />
    <circle cx="50" cy="50" r="9.5" fill="#C8AA6E" />
  </g>
</svg>
