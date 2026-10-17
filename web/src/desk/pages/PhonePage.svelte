<script lang="ts">
  // Everything about the phone link-up: whether phones may reach this PC, pairing by QR, paired devices, sign-in and alerts.
  import DeviceList from '../../lib/sections/DeviceList.svelte'
  import Network from '../../lib/sections/Network.svelte'
  import Pairing from '../../lib/sections/Pairing.svelte'
  import Password from '../../lib/sections/Password.svelte'
  import PhoneAlerts from '../../lib/sections/PhoneAlerts.svelte'
</script>

<header class="page-head">
  <span class="eyebrow">Away from the PC</span>
  <h1 class="display">Phone</h1>
  <p>Control Glamour from your phone.</p>
</header>

<div class="cols">
  <div class="col"><Network /><Pairing /><Password /></div>
  <div class="col"><DeviceList /><PhoneAlerts /></div>
</div>

<style>
  .cols { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 14px; align-items: start; }
  .col { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
  @media (max-width: 980px) { .cols { grid-template-columns: minmax(0, 1fr); } }
</style>
