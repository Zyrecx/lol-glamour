<script lang="ts">
  // The fun side: what friends see (rank and status message) the profile icon and the three challenge tokens.
  import Presence from '../../lib/sections/Presence.svelte'
  import ScopeBar from '../../lib/sections/ScopeBar.svelte'
  import ProfileIcon from '../../lib/sections/ProfileIcon.svelte'
  import Tokens from '../../lib/sections/Tokens.svelte'
</script>

<header class="page-head">
  <span class="eyebrow">Show off</span>
  <h1 class="display">Profile</h1>
</header>

<div class="cols">
  <div class="col"><ScopeBar /><Presence /></div>
  <div class="col"><ProfileIcon /><Tokens /></div>
</div>

<style>
  .cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 14px; align-items: start; }
  .col { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
</style>
