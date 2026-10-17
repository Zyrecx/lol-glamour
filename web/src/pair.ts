import { mount } from 'svelte'
import './app.css'
import Pair from './Pair.svelte'

export default mount(Pair, { target: document.getElementById('app')! })
