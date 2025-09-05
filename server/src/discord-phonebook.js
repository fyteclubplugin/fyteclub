// Use Discord as free phone book service
const { Client, GatewayIntentBits } = require('discord.js');

class DiscordPhoneBook {
    constructor(token, channelId) {
        this.client = new Client({ 
            intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildMessages] 
        });
        this.channelId = channelId;
        this.playerMap = new Map();
    }

    async start() {
        await this.client.login(process.env.DISCORD_BOT_TOKEN);
        const channel = await this.client.channels.fetch(this.channelId);
        
        // Listen for registration messages
        this.client.on('messageCreate', async (message) => {
            if (message.channel.id !== this.channelId) return;
            if (message.author.bot) return;
            
            try {
                const data = JSON.parse(message.content);
                if (data.type === 'register') {
                    this.playerMap.set(data.playerName, {
                        offer: data.offer,
                        timestamp: Date.now()
                    });
                    await message.react('✅');
                }
            } catch (e) {
                // Ignore non-JSON messages
            }
        });
    }

    async register(playerName, webrtcOffer) {
        const channel = await this.client.channels.fetch(this.channelId);
        await channel.send(JSON.stringify({
            type: 'register',
            playerName,
            offer: webrtcOffer
        }));
    }

    async lookup(playerName) {
        return this.playerMap.get(playerName);
    }
}

module.exports = DiscordPhoneBook;