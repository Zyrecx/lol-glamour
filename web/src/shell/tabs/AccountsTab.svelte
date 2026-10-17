<script lang="ts">
  import Accounts from '../../lib/sections/Accounts.svelte'
</script>

<div class="tab rise">
  <h1 class="display">Accounts</h1>
  <Accounts />
</div>

<style>
  .tab { display: flex; flex-direction: column; gap: 12px; }
  h1 { font-size: 24px; padding-top: 4px; }
</style>
