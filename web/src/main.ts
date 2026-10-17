import { mount } from 'svelte'
import './app.css'
import App from './App.svelte'
import { installClientLog } from './lib/clientLog'

installClientLog()

export default mount(App, { target: document.getElementById('app')! })
