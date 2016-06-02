from flask import Flask
from modstats.api import api

app = Flask(__name__)
app.register_blueprint(api)