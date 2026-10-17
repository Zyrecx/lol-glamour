<script lang="ts">
  // The client's tier emblem (CommunityDragon `ranked-emblem/emblem-<tier>.png`, cropped into 4:3 images in /ranks; the LCU does not serve it).
  let { tier, size = 64 }: { tier: string; size?: number } = $props()
</script>

<img src={`/ranks/${tier.toLowerCase()}.png`} alt="" width={size} height={Math.round(size * 0.75)} />

<style>
  img { display: block; flex: none; object-fit: contain; }
</style>
