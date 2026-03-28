import { Component, signal, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { CarService, Car, CreateCar } from '../../services/car.service';

@Component({
  selector: 'app-cars',
  imports: [NavbarComponent, RouterLink, FormsModule],
  templateUrl: './cars.page.html'
})
export class CarsPage implements OnInit {
  cars = signal<Car[]>([]);
  showForm = signal(false);
  newCar: CreateCar = { name: '', licensePlate: '' };

  constructor(private carService: CarService) {}

  ngOnInit() { this.load(); }

  load() { this.carService.getAll().subscribe(cars => this.cars.set(cars)); }

  cancelForm() {
    this.showForm.set(false);
    this.newCar = { name: '', licensePlate: '' };
  }

  onCreate() {
    this.carService.create({ name: this.newCar.name, licensePlate: this.newCar.licensePlate || undefined }).subscribe(() => {
      this.cancelForm();
      this.load();
    });
  }

  onToggleActive(car: Car) {
    this.carService.toggleActive(car.id).subscribe(() => this.load());
  }

  onDelete(car: Car) {
    if (confirm(`Natrvalo odstrániť vozidlo "${car.name}"? Záznamy dochádzky zostanú, ale budú bez vozidla.`)) {
      this.carService.delete(car.id).subscribe(() => this.load());
    }
  }
}
